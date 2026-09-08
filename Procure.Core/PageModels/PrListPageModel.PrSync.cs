using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Procure.Models;
using Procure.Utilities;

namespace Procure.PageModels
{
    // Keeping a requisition and the quotes hanging off it in step.
    //
    // Until this existed, a PR and its RFQs only agreed at the moment the RFQ was created. Every
    // later edit drifted them apart silently: an item added to the PR never appeared on a quote
    // already out, a quantity raised from 10 to 40 left every quote priced for 10, a renamed line
    // left the old wording on the paperwork, and a deleted line left its quote lines behind, still
    // priced and still counting towards that vendor's total in the PCR comparison.
    public partial class PrListPageModel
    {
        /// <summary>PR lines the user removed in this editing session that had quote lines against
        /// them, and agreed to have those quote lines removed with. Applied at save.
        ///
        /// No snapshot of the pre-edit lines is kept: the modal edits copies, so the live PR still
        /// holds them right up to the save, which is what the sync resolves against.</summary>
        private readonly HashSet<Guid> _prItemsToPurgeFromQuotes = new();

        private void CapturePrItemsSnapshot(PurchaseRequisition pr) => _prItemsToPurgeFromQuotes.Clear();

        /// <summary>A quote is "open" while no PO has been raised from it. Open quotes follow the
        /// requisition; once an order exists, the quote is part of that order's paper trail and is
        /// left exactly as it was.</summary>
        private static bool IsQuoteOpen(PurchaseRequisition pr, RequestForQuotation rfq) =>
            pr.Pos == null || !pr.Pos.Any(p => p.LinkedRfqId.HasValue && p.LinkedRfqId.Value == rfq.Id);

        /// <summary>Every PO line anywhere on this PR that is ordering the given PR line.</summary>
        private static IEnumerable<(PurchaseOrder Po, PurchaseOrderItem Line)> OrdersFor(PurchaseRequisition pr, PrItem prItem)
        {
            if (pr.Pos == null) yield break;
            foreach (var po in pr.Pos)
            {
                if (po.Items == null || po.Items.Count == 0) continue;
                foreach (var pair in PrLineMatcher.Map(po.Items, pr.Items))
                {
                    if (pair.Value.Id == prItem.Id) yield return (po, pair.Key);
                }
            }
        }

        /// <summary>Every quote line anywhere on this PR that is quoting the given PR line.</summary>
        private static IEnumerable<(RequestForQuotation Rfq, RfqItem Line)> QuotesFor(PurchaseRequisition pr, PrItem prItem)
        {
            if (pr.Rfqs == null) yield break;
            foreach (var rfq in pr.Rfqs)
            {
                if (rfq.Items == null || rfq.Items.Count == 0) continue;
                foreach (var pair in PrLineMatcher.Map(rfq.Items, pr.Items))
                {
                    if (pair.Value.Id == prItem.Id) yield return (rfq, pair.Key);
                }
            }
        }

        /// <summary>Test seams for ProcurementFlowSelfCheck. The guards themselves put a dialog on
        /// screen, which an unattended run can never answer, so the check exercises the decisions
        /// behind them - what is ordered, what is quoted - and the sync they gate.</summary>
        internal Task<string> RunQuoteSyncForTestAsync(PurchaseRequisition pr, List<PrItem> oldItems, List<PrItem> newItems)
            => SyncQuotesToPrAsync(pr, oldItems, newItems);

        internal static int OrderedLineCountForTest(PurchaseRequisition pr, PrItem prItem) => OrdersFor(pr, prItem).Count();

        internal static int QuotedLineCountForTest(PurchaseRequisition pr, PrItem prItem) => QuotesFor(pr, prItem).Count(q => IsPriced(q.Line));

        internal void MarkForQuotePurgeForTest(Guid prItemId) => _prItemsToPurgeFromQuotes.Add(prItemId);

        private static bool IsPriced(RfqItem line) =>
            line.IsQuoted && ((line.QuotedUnitPrice ?? 0m) > 0m || line.LineTotal > 0m);

        /// <summary>Guard for removing a line in the edit modal. An ordered line is refused outright
        /// - a raised PO says that quantity was bought, and quietly dropping the line it belongs to
        /// turns a real order into an untraceable one. A quoted line asks first and, on yes, the
        /// quote lines go with it at save time.</summary>
        private async Task<bool> ConfirmRemovePrItemAsync(PrItem item)
        {
            var pr = CurrentEditingPr;
            if (pr == null || false) return true;

            // Match against what the PR actually has saved, not the modal's working copy.
            var saved = pr.Items?.FirstOrDefault(i => i.Id == item.Id);
            if (saved == null) return true; // added in this session, nothing downstream can reference it

            var orders = OrdersFor(pr, saved).ToList();
            if (orders.Count > 0)
            {
                var poNos = string.Join(", ", orders.Select(o => o.Po.PoNo).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
                await _dialogs.DisplayAlertAsync(
                    "Item Already Ordered",
                    $"'{saved.ItemName}' is on purchase order {poNos}. Cancel or edit that PO first, then remove the line.",
                    "OK");
                return false;
            }

            var quoted = QuotesFor(pr, saved).Where(q => IsPriced(q.Line)).ToList();
            if (quoted.Count == 0) return true;

            var vendors = string.Join(", ", quoted.Select(q => q.Rfq.Vendor).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct());
            var removeQuotes = await _dialogs.DisplayAlertAsync(
                "Item Is Quoted",
                $"{quoted.Count} quote line(s) price '{saved.ItemName}' ({vendors}). Remove those quote lines too?\n\nLeaving them keeps their prices in each vendor's total and in the price comparison.",
                "Remove them",
                "Keep them");

            if (removeQuotes) _prItemsToPurgeFromQuotes.Add(saved.Id);
            return true;
        }

        /// <summary>Warns before saving a quantity smaller than what has already been ordered. Not a
        /// refusal - a requisition legitimately gets cut back after the fact, and the PR then carries
        /// a visible "Over-ordered" state rather than the old cheerful "Complete".</summary>
        private async Task<bool> ConfirmQuantityCutsAsync(PurchaseRequisition pr, List<PrItem> newItems)
        {
            if (false || pr.Items == null || pr.Items.Count == 0) return true;

            var ordered = PrLineMatcher.OrderedQuantities(pr.Items, pr.Pos);
            var cuts = new List<string>();
            foreach (var item in newItems)
            {
                if (!ordered.TryGetValue(item.Id, out var alreadyOrdered) || alreadyOrdered <= item.Quantity) continue;
                var unit = string.IsNullOrWhiteSpace(item.Unit) ? "pcs" : item.Unit;
                cuts.Add($"• {item.ItemName}: {alreadyOrdered:G29} {unit} already ordered, requisition would ask for {item.Quantity:G29}");
            }

            if (cuts.Count == 0) return true;

            return await _dialogs.DisplayAlertAsync(
                "Less Than Already Ordered",
                string.Join("\n", cuts) + "\n\nSave anyway? The line will be marked over-ordered.",
                "Save anyway",
                "Go back");
        }

        /// <summary>Pushes the requisition's changes down into every open quote, and returns a short
        /// human summary for the toast.
        ///
        /// Runs AFTER the requisition itself is written. A quote line points at a PR line by a foreign
        /// key, so a line added to the PR has to exist in the database before any quote line can
        /// reference it - doing this first threw "FOREIGN KEY constraint failed" and lost the whole
        /// save. <paramref name="oldItems"/> is therefore passed in: it is the pre-edit lines these
        /// quotes were written against, captured by the caller before the new list replaced them, and
        /// it is the only way a rename reads as a rename rather than as one line vanishing and an
        /// unrelated one appearing.</summary>
        private async Task<string> SyncQuotesToPrAsync(PurchaseRequisition pr, List<PrItem> oldItems, List<PrItem> newItems)
        {
            if (pr.Rfqs == null || pr.Rfqs.Count == 0) return string.Empty;

            var newById = newItems.ToDictionary(i => i.Id);

            int addedLines = 0, retitled = 0, requantified = 0, purged = 0, needsRepricing = 0;
            var touchedRfqs = new List<RequestForQuotation>();

            foreach (var rfq in pr.Rfqs)
            {
                if (!IsQuoteOpen(pr, rfq)) continue;
                if (rfq.Items == null) continue;

                var changed = false;

                // Resolve against the PRE-edit lines: that is what these quote lines were written
                // against, and it is the only way a rename can be recognised as a rename rather than
                // as one line vanishing and an unrelated one appearing.
                var map = PrLineMatcher.Map(rfq.Items, oldItems);

                for (int i = rfq.Items.Count - 1; i >= 0; i--)
                {
                    var line = rfq.Items[i];
                    if (!map.TryGetValue(line, out var oldLine)) continue;

                    // Removed from the requisition, and the user asked for the quote lines to go too.
                    if (!newById.ContainsKey(oldLine.Id))
                    {
                        if (_prItemsToPurgeFromQuotes.Contains(oldLine.Id))
                        {
                            rfq.Items.RemoveAt(i);
                            purged++;
                            changed = true;
                        }
                        continue;
                    }

                    var updated = newById[oldLine.Id];

                    if (!PrLineMatcher.NameEquals(line.ItemName, updated.ItemName))
                    {
                        line.ItemName = updated.ItemName;
                        retitled++;
                        changed = true;
                    }

                    if (line.Quantity != updated.Quantity)
                    {
                        if (IsPriced(line))
                        {
                            // Keep the number the vendor actually quoted. The card flags it instead
                            // (RequestForQuotation.QuantityDriftBadge) so it gets re-asked.
                            needsRepricing++;
                        }
                        else
                        {
                            line.Quantity = updated.Quantity;
                            line.Unit = updated.Unit;
                            requantified++;
                            changed = true;
                        }
                    }

                    // The link itself: a line that was only ever found by its text is pinned now, so
                    // the next rename cannot detach it.
                    if (!line.PrItemId.HasValue)
                    {
                        line.PrItemId = oldLine.Id;
                        changed = true;
                    }
                }

                // Lines the requisition has that this quote does not. Added unpriced, which changes
                // no money (only priced lines count towards a quote's total) and shows up as
                // "Partial (1 of 2)" until someone fills the price in.
                var covered = new HashSet<Guid>(PrLineMatcher.Map(rfq.Items, newItems).Values.Select(v => v.Id));
                var sortOrder = rfq.Items.Count == 0 ? 0 : rfq.Items.Max(x => x.SortOrder) + 1;
                foreach (var item in newItems)
                {
                    if (covered.Contains(item.Id)) continue;

                    rfq.Items.Add(new RfqItem
                    {
                        Id = Guid.NewGuid(),
                        RfqId = rfq.Id,
                        PrItemId = item.Id,
                        ItemName = item.ItemName,
                        Quantity = item.Quantity,
                        Unit = item.Unit,
                        IsQuoted = true,
                        QuotedUnitPrice = null,
                        LastPrice = item.EstimatedUnitPrice,
                        SortOrder = sortOrder++
                    });
                    addedLines++;
                    changed = true;
                }

                if (changed)
                {
                    touchedRfqs.Add(rfq);
                }
            }

            foreach (var rfq in touchedRfqs)
            {
                await _prRepo.SaveRfqAsync(rfq);
                rfq.NotifyCalculationsChanged();
            }

            // Ordered lines keep their wording: a raised PO is a document that went out, and it must
            // not silently rewrite itself. Their link is still pinned so the numbers stay right.
            if (pr.Pos != null)
            {
                foreach (var po in pr.Pos)
                {
                    var pinned = false;
                    foreach (var pair in PrLineMatcher.Map(po.Items, oldItems))
                    {
                        if (pair.Key.PrItemId.HasValue) continue;
                        pair.Key.PrItemId = pair.Value.Id;
                        pinned = true;
                    }
                    if (pinned) await _prRepo.SavePoAsync(po);
                }
            }

            return BuildSyncSummary(touchedRfqs.Count, addedLines, retitled, requantified, purged, needsRepricing);
        }

        private static string BuildSyncSummary(int quoteCount, int added, int retitled, int requantified, int purged, int needsRepricing)
        {
            if (quoteCount == 0 && needsRepricing == 0) return string.Empty;

            var parts = new List<string>(4);
            if (added > 0) parts.Add(added == 1 ? "1 line added" : $"{added} lines added");
            if (requantified > 0) parts.Add(requantified == 1 ? "1 quantity updated" : $"{requantified} quantities updated");
            if (retitled > 0) parts.Add(retitled == 1 ? "1 description updated" : $"{retitled} descriptions updated");
            if (purged > 0) parts.Add(purged == 1 ? "1 line removed" : $"{purged} lines removed");

            var head = parts.Count == 0
                ? string.Empty
                : $"{string.Join(", ", parts)} across {(quoteCount == 1 ? "1 quote" : $"{quoteCount} quotes")}";

            if (needsRepricing > 0)
            {
                var tail = needsRepricing == 1 ? "1 quoted line needs re-pricing" : $"{needsRepricing} quoted lines need re-pricing";
                return string.IsNullOrEmpty(head) ? tail : $"{head} · {tail}";
            }

            return head;
        }
    }
}
