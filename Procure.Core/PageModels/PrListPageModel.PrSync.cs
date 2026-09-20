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

        internal Task<string> RunOrderSyncForTestAsync(PurchaseRequisition pr, RequestForQuotation rfq, List<RfqItem> preEditLines)
            => SyncOrdersToQuoteAsync(pr, rfq, preEditLines);

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

            int addedLines = 0, retitled = 0, requantified = 0, purged = 0, needsRepricing = 0, lastPriceUpdated = 0;
            var touchedRfqs = new List<RequestForQuotation>();

            foreach (var rfq in pr.Rfqs)
            {
                if (rfq.Items == null) continue;

                // Lines never read cannot be reconciled: every requisition line would look missing
                // and be added a second time. The caller hydrates first (EnsureHydratedAsync); this
                // is the backstop if one ever slips through.
                if (!rfq.ItemsLoaded) continue;

                // A quote with an order against it still takes the fields the user actually edited -
                // a corrected description or historical price belongs on it too. What it does not
                // take is structure: no line is added to it and none is removed from it.
                var quoteOpen = IsQuoteOpen(pr, rfq);

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
                        if (quoteOpen && _prItemsToPurgeFromQuotes.Contains(oldLine.Id))
                        {
                            rfq.Items.RemoveAt(i);
                            purged++;
                            changed = true;
                        }
                        continue;
                    }

                    var updated = newById[oldLine.Id];

                    // Field by field, and only the fields the user actually changed in the edit
                    // dialog: each is compared against the PRE-edit requisition line, never against
                    // what the quote happens to hold. A box left alone upstream changes nothing
                    // downstream, so the vendor's own wording, quantity or price survives untouched.
                    if (!PrLineMatcher.NameEquals(oldLine.ItemName, updated.ItemName))
                    {
                        line.ItemName = updated.ItemName;
                        retitled++;
                        changed = true;
                    }

                    // The requisition's estimated price is the reference figure the quote shows as
                    // "Last Price" - the requisition's number, not the vendor's. It reaches every
                    // quote, priced or ordered; the vendor's own columns are what stay off limits.
                    // The one place a value travels down without having just been edited: a quote line
                    // that has never carried a last price at all. Quotes written before this synced
                    // would otherwise show an empty column until someone typed the estimate twice.
                    if (oldLine.EstimatedUnitPrice != updated.EstimatedUnitPrice
                        || (line.LastPrice is null && updated.EstimatedUnitPrice.HasValue))
                    {
                        line.LastPrice = updated.EstimatedUnitPrice;
                        line.LastPriceCurrency = updated.EstimatedCurrency;
                        lastPriceUpdated++;
                        changed = true;
                    }

                    if (oldLine.Quantity != updated.Quantity)
                    {
                        // Only where this quote was asking for the requisition's quantity. A vendor
                        // who quoted a part of it (1,000 of the 3,000 asked for) chose that number
                        // themselves, and overwriting it would destroy what they actually offered -
                        // the quote is flagged for a re-ask instead.
                        if (line.Quantity == oldLine.Quantity)
                        {
                            line.Quantity = updated.Quantity;
                            requantified++;
                            changed = true;
                        }
                        else
                        {
                            needsRepricing++;
                        }
                    }

                    if (!string.Equals(oldLine.Unit, updated.Unit, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(updated.Unit))
                    {
                        line.Unit = updated.Unit;
                        changed = true;
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
                // "Partial (1 of 2)" until someone fills the price in. Only onto an open quote: an
                // order has been raised against the other kind, and a document that went out does
                // not grow a line by itself.
                var covered = quoteOpen
                    ? new HashSet<Guid>(PrLineMatcher.Map(rfq.Items, newItems).Values.Select(v => v.Id))
                    : new HashSet<Guid>(newItems.Select(i => i.Id));
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

            return BuildSyncSummary(touchedRfqs.Count, addedLines, retitled, requantified, purged, needsRepricing, lastPriceUpdated);
        }

        /// <summary>The same rule one step further down: what the user changed on a quote line reaches
        /// the order lines raised from it, field by field, and nothing else does. An order line that
        /// carries its own figure - half the quoted quantity, a negotiated rate - keeps it, because
        /// that number was entered on the order and the order is the document that went out.
        ///
        /// <paramref name="preEditLines"/> is the quote's lines as they were before this save, which
        /// is what makes "the user changed this box" answerable at all.</summary>
        private async Task<string> SyncOrdersToQuoteAsync(PurchaseRequisition pr, RequestForQuotation rfq,
                                                          IReadOnlyList<RfqItem> preEditLines)
        {
            if (pr.Pos == null || pr.Pos.Count == 0 || rfq.Items == null) return string.Empty;

            var oldById = preEditLines.ToDictionary(i => i.Id);
            int lines = 0, kept = 0;
            var touchedPos = new List<PurchaseOrder>();

            foreach (var po in pr.Pos)
            {
                if (po.Items == null || po.Items.Count == 0) continue;
                if (!po.ItemsLoaded) continue;              // never delete or rewrite what was not read
                if (po.LinkedRfqId != rfq.Id) continue;     // an order raised from a different quote

                var changed = false;

                foreach (var poLine in po.Items)
                {
                    if (!poLine.RfqItemId.HasValue) continue;
                    if (!oldById.TryGetValue(poLine.RfqItemId.Value, out var before)) continue;

                    var after = rfq.Items.FirstOrDefault(i => i.Id == poLine.RfqItemId.Value);
                    if (after == null) continue;            // the quote line is gone; the order keeps its own

                    var touched = false;

                    if (!PrLineMatcher.NameEquals(before.ItemName, after.ItemName))
                    {
                        poLine.ItemName = after.ItemName;
                        touched = true;
                    }

                    if (!string.Equals(before.Unit, after.Unit, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(after.Unit))
                    {
                        poLine.Unit = after.Unit;
                        touched = true;
                    }

                    if (before.Quantity != after.Quantity)
                    {
                        // Only an order that was taking the whole quoted quantity follows it. A part
                        // order (500 of the 1,000 quoted) is its own decision.
                        if (poLine.Quantity == before.Quantity)
                        {
                            poLine.Quantity = after.Quantity;
                            touched = true;
                        }
                        else kept++;
                    }

                    // A price that became words ("Regret") or was simply cleared is NOT pushed down:
                    // an order that was placed at a rate keeps that rate, rather than losing its
                    // value because the quote it came from no longer states one.
                    if (before.QuotedUnitPrice != after.QuotedUnitPrice && after.QuotedUnitPrice.HasValue)
                    {
                        if (poLine.UnitPrice == before.QuotedUnitPrice)
                        {
                            poLine.UnitPrice = after.QuotedUnitPrice;
                            touched = true;
                        }
                        else kept++;
                    }

                    if (before.Discount != after.Discount)
                    {
                        if (poLine.Discount == before.Discount)
                        {
                            poLine.Discount = after.Discount;
                            touched = true;
                        }
                        else kept++;
                    }

                    if (touched) { lines++; changed = true; }
                }

                if (!changed) continue;

                // The order's money is rebuilt from its own lines, exactly as the PO wizard does it:
                // base, then freight and other charges in, discount out, then VAT on what is left.
                var baseAmount = po.Items.Sum(i => i.LineTotal);
                var net = Math.Max(0m, baseAmount + (po.Freight ?? 0m) + (po.OtherCharges ?? 0m) - (po.Discount ?? 0m));
                po.BaseAmount = baseAmount;
                po.Value = net + (po.VatType == "5%" ? net * 0.05m : 0m);

                touchedPos.Add(po);
            }

            foreach (var po in touchedPos)
            {
                await _prRepo.SavePoAsync(po);
                po.NotifyCalculationsChanged();
            }

            if (touchedPos.Count == 0) return string.Empty;

            var orderWord = touchedPos.Count == 1 ? "1 order" : $"{touchedPos.Count} orders";
            var lineWord = lines == 1 ? "1 line" : $"{lines} lines";
            var summary = $"{lineWord} updated on {orderWord}";
            if (kept > 0) summary += kept == 1 ? " · 1 order line kept its own figure" : $" · {kept} order lines kept their own figures";
            return summary;
        }

        private static string BuildSyncSummary(int quoteCount, int added, int retitled, int requantified, int purged, int needsRepricing, int lastPriceUpdated)
        {
            if (quoteCount == 0 && needsRepricing == 0) return string.Empty;

            var parts = new List<string>(4);
            if (added > 0) parts.Add(added == 1 ? "1 line added" : $"{added} lines added");
            if (requantified > 0) parts.Add(requantified == 1 ? "1 quantity updated" : $"{requantified} quantities updated");
            if (retitled > 0) parts.Add(retitled == 1 ? "1 description updated" : $"{retitled} descriptions updated");
            if (purged > 0) parts.Add(purged == 1 ? "1 line removed" : $"{purged} lines removed");
            if (lastPriceUpdated > 0) parts.Add(lastPriceUpdated == 1 ? "1 last price updated" : $"{lastPriceUpdated} last prices updated");

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
