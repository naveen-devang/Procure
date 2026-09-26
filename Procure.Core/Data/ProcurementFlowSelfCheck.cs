using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.Data
{
    /// <summary>
    /// End-to-end exercise of the whole procurement chain against a real database: requisition,
    /// quote, order, merge, split, shared quote, combined order, and the round-2 rules that keep a
    /// requisition and its quotes in step.
    ///
    /// The existing checks each guard one thing (DatabaseSelfCheck the search column,
    /// CallOffSelfCheck the material aggregates, PrLineMatcherSelfCheck the pairing rules in
    /// isolation). This one runs the flows a person actually performs, end to end, through the real
    /// repository, and re-asserts the derived-data invariants after every write - which is where a
    /// missed write path shows up.
    ///
    /// It also records what each phase costs: wall time, managed heap, working set and how many
    /// garbage collections it caused, written next to the database as procurement-flow-metrics.csv.
    /// Run it against the 20,000-PR database so those numbers mean something:
    ///
    ///     PROCURE_FLOW_SELFCHECK=1  PROCURE_DB_DIR=C:\...\procure-20k  Procure.exe
    ///
    /// Everything it creates is prefixed with a per-run marker and deleted in the finally block, so
    /// a completed run leaves the database exactly as it found it. It writes to whatever database it
    /// is pointed at, so it is Debug only and opt-in only - never point it at real data.
    ///
    /// What it deliberately does NOT cover: the confirmation dialogs (removing a quoted line,
    /// deleting a PR that has orders). Those need someone to answer them, so the check exercises the
    /// decisions behind them instead - what is ordered, what is quoted - through the seams on
    /// PrListPageModel.
    /// </summary>
    internal static class ProcurementFlowSelfCheck
    {
        private const string Brush = "CARBON BRUSH,25X50X50MM F/FRAME:M15E";
        private const string Gasket = "GASKET,SPIRAL WOUND,DN50,CL150";
        private const string Gypsum = "GYPSUM";

        /// <summary>The host's container. Set by whichever head is running the check - this used to
        /// read MAUI's IPlatformApplication.Current directly, which is why the whole suite could only
        /// ever run from the MAUI app. One phase needs the board's page model; the rest are pure
        /// repository work.</summary>
        public static IServiceProvider? HostServices { get; set; }

        private static readonly List<Phase> Phases = new();
        private static readonly List<string> Failures = new();
        private static readonly List<Guid> Created = new();
        private static string _marker = string.Empty;
        private static int _assertions;

        private sealed record Phase(string Name, double Ms, long HeapDeltaKb, long HeapKb, long WorkingSetMb,
            int Gen0, int Gen1, int Gen2, int SawBlockingGen2, long LohKb, double PausePct, int Checks);

        public static async Task RunAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            _marker = "flow-" + Guid.NewGuid().ToString("N")[..6];
            Phases.Clear();
            Failures.Clear();
            Created.Clear();
            _assertions = 0;

            // The Todo and Note checks force collections outright (GC.Collect + WaitForPendingFinalizers,
            // twice each) because they are leak tests. Run alongside this one, those forced collections
            // land in these phases' counters and make the app look like it is collecting constantly -
            // which is exactly how a healthy 7/5/4 first got read as an alarming 13/10/10. Say so in
            // the log rather than publish a number that cannot be trusted.
            var contaminated = Environment.GetEnvironmentVariable("PROCURE_TODO_SELFCHECK") == "1"
                            || Environment.GetEnvironmentVariable("PROCURE_NOTE_SELFCHECK") == "1"
                            || Environment.GetEnvironmentVariable("PROCURE_BOARD_SELFCHECK") == "1";

            var total = Stopwatch.StartNew();
            try
            {
                await Measure("00 baseline read (20k board page)", () => BoardPageReadAsync(repo));
                await Measure("01 PR create / read back / edit", () => PrLifecycleAsync(db, repo));
                await Measure("02 RFQ create, line add, coverage", () => RfqFlowAsync(db, repo));
                await Measure("03 PO allocation and fulfilment", () => PoFlowAsync(db, repo));
                await Measure("04 PO edit round trip", () => PoEditRoundTripAsync(db, repo));
                await Measure("05 merge with duplicate item names", () => MergeFlowAsync(db, repo));
                await Measure("06 unmerge / partial split", () => SplitFlowAsync(db, repo));
                await Measure("07 shared RFQ across PRs", () => SharedRfqFlowAsync(db, repo));
                await Measure("08 combined PO across PRs", () => CombinedPoFlowAsync(db, repo));
                await Measure("09 PR edit syncs into open quotes", () => QuoteSyncFlowAsync(db, repo));
                await Measure("09b board refresh then PR edit keeps prices", () => RefreshThenEditKeepsPricesAsync(db, repo));
                await Measure("09c one edit travels one hop (PR->RFQ->PO)", () => FieldLevelDownstreamSyncAsync(db, repo));
                await Measure("09d words instead of a price on a quote line", () => PriceNoteFlowAsync(db, repo));
                await Measure("09e historical price currency travels with it", () => HistoricalPriceCurrencyFlowAsync(db, repo));
                await Measure("10 guard decisions (ordered / quoted)", () => GuardDecisionsAsync(db, repo));
                await Measure("11 deletes and cascade", () => DeleteFlowAsync(db, repo));
                await Measure("12 PCR export (Excel + PDF)", () => PcrExportFlowAsync(db, repo));
                await Measure("13 concurrent saves on one material", () => ConcurrentMaterialSavesAsync(db, repo));
                await Measure("14 undo window hides, undo restores, expiry deletes", () => UndoDeleteFlowAsync(db, repo));
                await Measure("15 vendor suggestions", () => VendorSuggestionFlowAsync(db, repo));
                await Measure("16 last price paid", () => LastPaidPriceFlowAsync(db, repo));
                await Measure("17 suppliers page", () => SuppliersFlowAsync(db, repo));
            }
            catch (Exception ex)
            {
                Failures.Add("ABORTED: " + ex);
            }
            finally
            {
                foreach (var id in Created.AsEnumerable().Reverse())
                {
                    try { await repo.DeleteAsync(id); } catch { /* cleanup is best effort */ }
                }
                total.Stop();
                Report(total.Elapsed.TotalSeconds, contaminated);
            }
        }

        // ---- phases ---------------------------------------------------------------------------

        /// <summary>The board's own read path at whatever size the database is, so the metrics line
        /// up with what the app does on launch rather than with the test data this check creates.</summary>
        private static async Task BoardPageReadAsync(IPurchaseRequisitionRepository repo)
        {
            var page = await repo.GetPageAsync(new PrQuery(null, null, false, false, false, 10, 5, 0, 50));
            Assert(page.Rows.Count > 0, $"the board returns rows; got {page.Rows.Count}");
            Assert(page.TotalCount >= page.Rows.Count, "the unpaged count is at least the page size");

            // Search goes through the denormalised column; exercising it here means the metrics
            // include it and a broken blob shows up as zero matches rather than silently later.
            // A single letter is the worst case the search box can be given: it matches most of the
            // database, and it is what the first keystroke sends. Timed, because the cost of getting
            // this wrong does not show up as a failure anywhere else - an earlier query plan searched
            // the index once per candidate requisition and took 117 SECONDS here.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var search = await repo.GetPageAsync(new PrQuery("a", null, false, false, false, 10, 5, 0, 25));
            sw.Stop();
            Assert(search.TotalCount >= 0, "search completes");
            Assert(sw.Elapsed.TotalSeconds < 3,
                $"a one-letter search stays interactive; took {sw.Elapsed.TotalSeconds:F1}s for {search.TotalCount} matches");
        }

        private static async Task PrLifecycleAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var pr = NewPr("pr-lifecycle", (Brush, 12m), (Gasket, 4m));
            await repo.SaveAsync(pr);
            await AssertDerivedDataFreshAsync(db, "after creating a PR");

            var read = await Reload(repo, pr.Id);
            Assert(read.Items.Count == 2, $"both lines come back; got {read.Items.Count}");
            Assert(read.Items[0].Id == pr.Items[0].Id, "line ids survive the round trip (links depend on it)");
            Assert(read.Items.Sum(i => i.Quantity) == 16m, $"quantities survive; got {read.Items.Sum(i => i.Quantity)}");

            // The edit path: rename one line, re-quantify the other, add a third.
            read.Items[0].ItemName = Brush + " (REV A)";
            read.Items[1].Quantity = 9m;
            read.Items.Add(new PrItem { Id = Guid.NewGuid(), PrId = read.Id, ItemName = _marker + "-third", Quantity = 2m, Unit = "NOS", SortOrder = 2 });
            await repo.SaveAsync(read);
            await AssertDerivedDataFreshAsync(db, "after editing a PR");

            var again = await Reload(repo, pr.Id);
            Assert(again.Items.Count == 3, $"the added line persists; got {again.Items.Count}");
            Assert(again.Items.Any(i => i.ItemName.EndsWith("(REV A)", StringComparison.Ordinal)), "the rename persists");
            Assert(again.Items.First(i => i.ItemName == Gasket).Quantity == 9m, "the quantity change persists");

            // And removing one leaves the survivors' ids alone - a delete-and-reinsert would sever
            // every quote and order line hanging off them.
            var keptId = again.Items[0].Id;
            again.Items.RemoveAt(2);
            await repo.SaveAsync(again);
            var trimmed = await Reload(repo, pr.Id);
            Assert(trimmed.Items.Count == 2, $"the removed line is gone; got {trimmed.Items.Count}");
            Assert(trimmed.Items.Any(i => i.Id == keptId), "the surviving line keeps its id");
            await AssertDerivedDataFreshAsync(db, "after removing a PR line");
        }

        private static async Task RfqFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var pr = NewPr("rfq-flow", (Brush, 12m), (Gasket, 4m));
            await repo.SaveAsync(pr);

            // A quote that covers only the first line - the case the coverage chip exists for.
            var rfq = NewRfq(pr, "alpha");
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 15.84m));
            await repo.SaveRfqAsync(rfq);
            await AssertDerivedDataFreshAsync(db, "after saving a quote");

            var read = await Reload(repo, pr.Id);
            var readRfq = read.Rfqs.Single(r => r.Id == rfq.Id);
            Assert(readRfq.Items.Count == 1, $"the quote line persists; got {readRfq.Items.Count}");
            Assert(readRfq.Items[0].PrItemId == pr.Items[0].Id, "and keeps its link to the PR line");

            read.NotifyHierarchyChanged();
            Assert(readRfq.PrLineCount == 2, $"coverage sees both PR lines; got {readRfq.PrLineCount}");
            Assert(readRfq.PrLinesCovered == 1, $"the quote covers one; got {readRfq.PrLinesCovered}");
            Assert(readRfq.PrLinesMissing == 1, "so one is missing");
            Assert(readRfq.QuoteCompletenessBadge == "Partial (1 of 2)",
                $"the chip counts against the PR, not itself; got '{readRfq.QuoteCompletenessBadge}'");
            Assert(!readRfq.HasQuantityDrift, "nothing has drifted yet");

            // A line typed straight into the quote carries no link, by design.
            readRfq.Items.Add(new RfqItem
            {
                Id = Guid.NewGuid(),
                RfqId = readRfq.Id,
                PrItemId = null,
                ItemName = _marker + "-hand-added",
                Quantity = 7m,
                Unit = "NOS",
                IsQuoted = true,
                QuotedUnitPrice = 3m,
                SortOrder = 1
            });
            await repo.SaveRfqAsync(readRfq);
            await AssertDerivedDataFreshAsync(db, "after adding a quote line by hand");

            var withHand = await Reload(repo, pr.Id);
            var handRfq = withHand.Rfqs.Single(r => r.Id == rfq.Id);
            Assert(handRfq.Items.Count == 2, $"both quote lines persist; got {handRfq.Items.Count}");
            Assert(handRfq.Items.Count(i => i.PrItemId == null) == 1, "the hand-added line stays unlinked");
            Assert(handRfq.BaseAmount == 12m * 15.84m + 7m * 3m,
                $"the quote total sums both priced lines; got {handRfq.BaseAmount}");

            // Requisition moves after pricing: the quote keeps the vendor's number and says so.
            withHand.Items[0].Quantity = 20m;
            withHand.NotifyHierarchyChanged();
            Assert(handRfq.HasQuantityDrift, "a priced line whose PR quantity moved is flagged");
            Assert(handRfq.QuantityDriftBadge.Contains("1 item", StringComparison.Ordinal),
                $"and names how many; got '{handRfq.QuantityDriftBadge}'");
        }

        private static async Task PoFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            // The reported shape: two PR lines with identical text, 12 and 33.
            var pr = NewPr("po-flow", (Brush, 12m), (Brush, 33m));
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "assam");
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 15.84m));
            rfq.Items.Add(NewRfqLine(pr.Items[1], price: 15.84m));
            await repo.SaveRfqAsync(rfq);

            var read = await Reload(repo, pr.Id);
            var readRfq = read.Rfqs.Single();

            // The wizard's own model, built the way the wizard builds it.
            var card = new PoRfqSelection(readRfq, read, isSelected: true);
            Assert(card.Items.Count == 2, $"two rows, one per PR line; got {card.Items.Count}");
            Assert(card.Items[0].PrTargetQuantity == 12m && card.Items[1].PrTargetQuantity == 33m,
                $"targets are 12 and 33, not 12 and 12; got {card.Items[0].PrTargetQuantity} and {card.Items[1].PrTargetQuantity}");
            Assert(card.Items[0].Quantity == 12m && card.Items[1].Quantity == 33m,
                $"and the rows offer the full quantities; got {card.Items[0].Quantity} and {card.Items[1].Quantity}");
            Assert(!card.Items.Any(i => i.IsOverAllocated), "nothing is over-allocated before anything is ordered");

            // Order the 12 only.
            var po1 = NewPo(read, readRfq, "PO-A");
            po1.Items.Add(NewPoLine(pr.Items[0], 12m, 15.84m));
            await repo.SavePoAsync(po1);
            await AssertDerivedDataFreshAsync(db, "after raising a PO");

            var afterFirst = await Reload(repo, pr.Id);
            afterFirst.CalculateItemFulfillments();
            Assert(afterFirst.Items[0].OrderedQuantity == 12m, $"the ordered line reads 12; got {afterFirst.Items[0].OrderedQuantity}");
            Assert(afterFirst.Items[1].OrderedQuantity == 0m,
                $"its identically named sibling reads 0, not 12; got {afterFirst.Items[1].OrderedQuantity}");
            Assert(!afterFirst.IsPoFullyOrdered, "the PR is not complete on one of two lines");
            Assert(afterFirst.TotalPendingItemQuantity == 33m, $"33 still pending; got {afterFirst.TotalPendingItemQuantity}");

            // Reopening the wizard must not re-offer the 12.
            var reopened = new PoRfqSelection(afterFirst.Rfqs.Single(), afterFirst, isSelected: true);
            Assert(reopened.Items[0].OtherPosOrderedQuantity == 12m,
                $"the first row knows 12 is already on order; got {reopened.Items[0].OtherPosOrderedQuantity}");
            Assert(reopened.Items[0].Quantity == 0m, $"so it offers nothing more; got {reopened.Items[0].Quantity}");
            Assert(reopened.Items[1].Quantity == 33m, $"the second row still offers 33; got {reopened.Items[1].Quantity}");

            // Order the 33 as a separate PO.
            var po2 = NewPo(afterFirst, afterFirst.Rfqs.Single(), "PO-B");
            po2.Items.Add(NewPoLine(afterFirst.Items[1], 33m, 15.84m));
            await repo.SavePoAsync(po2);
            await AssertDerivedDataFreshAsync(db, "after a second PO on the same PR");

            var afterBoth = await Reload(repo, pr.Id);
            afterBoth.CalculateItemFulfillments();
            Assert(afterBoth.Items.All(i => i.IsFullyOrdered), "both lines are now fully ordered");
            Assert(afterBoth.IsPoFullyOrdered, "and so is the PR");
            Assert(!afterBoth.HasOverOrderedItems, "with nothing over-ordered");
            Assert(afterBoth.TotalOrderedItemQuantity == 45m, $"45 ordered in total; got {afterBoth.TotalOrderedItemQuantity}");

            // Cutting the requisition below what is ordered must not read as complete.
            afterBoth.Items[1].Quantity = 20m;
            afterBoth.CalculateItemFulfillments();
            Assert(afterBoth.Items[1].IsOverOrdered, "the cut line is over-ordered");
            Assert(afterBoth.Items[1].OverOrderedQuantity == 13m, $"by 13; got {afterBoth.Items[1].OverOrderedQuantity}");
            Assert(afterBoth.PoFulfillmentBadgeText.Contains("Over-ordered", StringComparison.Ordinal),
                $"and the PR badge says so instead of Complete; got '{afterBoth.PoFulfillmentBadgeText}'");
        }

        private static async Task PoEditRoundTripAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var pr = NewPr("po-edit", (Brush, 10m));
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "roundtrip");
            rfq.Freight = 390m;
            rfq.VatType = "5%";
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 20m));
            await repo.SaveRfqAsync(rfq);

            var read = await Reload(repo, pr.Id);
            var card = new PoRfqSelection(read.Rfqs.Single(), read, isSelected: true);
            var firstTotal = card.DisplayTotalAmount;
            Assert(firstTotal > 0m, "the card has a landed total");

            var po = NewPo(read, read.Rfqs.Single(), "PO-RT");
            po.Items.Add(NewPoLine(pr.Items[0], 10m, 20m));
            po.BaseAmount = card.BaseAmount;
            po.Freight = card.Freight;
            po.VatType = card.VatType;
            po.Value = card.DisplayTotalAmount;
            await repo.SavePoAsync(po);
            await AssertDerivedDataFreshAsync(db, "after saving a PO with a breakdown");

            var reloaded = await Reload(repo, pr.Id);
            var editCard = new PoRfqSelection(reloaded.Pos.Single(), reloaded.Rfqs.Single(), reloaded);

            Assert(Math.Abs(editCard.DisplayTotalAmount - firstTotal) < 0.01m,
                $"reopening the PO reproduces its total rather than re-applying VAT; expected {firstTotal:F2}, got {editCard.DisplayTotalAmount:F2}");
            Assert(editCard.Items.Count == 1, $"its line comes back; got {editCard.Items.Count}");
            Assert(editCard.Items[0].OtherPosOrderedQuantity == 0m,
                "and the PO being edited is not counted as an 'other' PO against itself");
            Assert(!editCard.Items[0].IsOverAllocated, "so it does not open falsely over-allocated");
        }

        private static async Task MergeFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            // Two requisitions asking for the same item - what produced the reported bug.
            var a = NewPr("merge-a", (Brush, 12m));
            var b = NewPr("merge-b", (Brush, 33m));
            await repo.SaveAsync(a);
            await repo.SaveAsync(b);

            var rfqA = NewRfq(a, "marrar");
            rfqA.Items.Add(NewRfqLine(a.Items[0], price: 15m));
            await repo.SaveRfqAsync(rfqA);

            var rfqB = NewRfq(b, "akber");
            rfqB.Items.Add(NewRfqLine(b.Items[0], price: 16m));
            await repo.SaveRfqAsync(rfqB);

            var sourceA = await Reload(repo, a.Id);
            var sourceB = await Reload(repo, b.Id);

            var master = new PurchaseRequisition
            {
                Id = Guid.NewGuid(),
                PrNo = _marker + "-master",
                Description = "merged",
                Requestor = "flow check",
                ConsolidatedFrom = $"{sourceA.PrNo}, {sourceB.PrNo}"
            };
            Created.Add(master.Id);

            await repo.MergePrsAsync(new List<PurchaseRequisition> { sourceA, sourceB }, master, copyRfqs: true);
            await AssertDerivedDataFreshAsync(db, "after merging two PRs");

            var merged = await Reload(repo, master.Id);
            Assert(merged.Items.Count == 2, $"the master keeps both lines separate; got {merged.Items.Count}");
            Assert(merged.Items.Sum(i => i.Quantity) == 45m, $"totalling 45; got {merged.Items.Sum(i => i.Quantity)}");
            Assert(merged.Rfqs.Count == 2, $"both quotes came across; got {merged.Rfqs.Count}");

            var allCopied = merged.Rfqs.SelectMany(r => r.Items).ToList();
            Assert(allCopied.Count == 2, $"with their lines; got {allCopied.Count}");
            Assert(allCopied.All(i => i.PrItemId.HasValue),
                "and every copied quote line carries its link - this is the bug that started all of it");

            var twelve = merged.Items.Single(i => i.Quantity == 12m);
            var thirtyThree = merged.Items.Single(i => i.Quantity == 33m);
            var marrarLine = merged.Rfqs.Single(r => r.Vendor.Contains("marrar", StringComparison.OrdinalIgnoreCase)).Items.Single();
            var akberLine = merged.Rfqs.Single(r => r.Vendor.Contains("akber", StringComparison.OrdinalIgnoreCase)).Items.Single();
            Assert(marrarLine.PrItemId == twelve.Id, "the 12 NOS quote points at the 12 NOS line");
            Assert(akberLine.PrItemId == thirtyThree.Id, "and the 33 NOS quote at the 33 NOS line");

            // And the wizard built on it offers 12 and 33, not 12 and 12.
            var card = new PoRfqSelection(merged.Rfqs.First(), merged, isSelected: true);
            var targets = card.Items.Select(i => i.PrTargetQuantity).OrderBy(q => q).ToList();
            Assert(targets.SequenceEqual(new[] { 12m, 33m }),
                $"the PO window sees both targets; got {string.Join(" and ", targets)}");

            var sourcesAfter = await repo.GetByIdsAsync(new[] { a.Id, b.Id });
            Assert(sourcesAfter.All(p => p.Status == ProcurementStatus.Merged), "the sources are marked merged");
            Assert(sourcesAfter.All(p => p.ParentPrId == master.Id), "and parented to the master");
        }

        private static async Task SplitFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var a = NewPr("split-a", (Brush, 12m));
            var b = NewPr("split-b", (Gasket, 4m));
            await repo.SaveAsync(a);
            await repo.SaveAsync(b);

            var master = new PurchaseRequisition
            {
                Id = Guid.NewGuid(),
                PrNo = _marker + "-split-master",
                Description = "to be split",
                Requestor = "flow check",
                ConsolidatedFrom = $"{a.PrNo}, {b.PrNo}"
            };
            Created.Add(master.Id);
            await repo.MergePrsAsync(new List<PurchaseRequisition> { await Reload(repo, a.Id), await Reload(repo, b.Id) }, master, copyRfqs: false);

            await repo.SplitMergedPrAsync(master.Id);
            await AssertDerivedDataFreshAsync(db, "after unmerging");

            var restored = await repo.GetByIdsAsync(new[] { a.Id, b.Id });
            Assert(restored.Count == 2, $"both originals come back; got {restored.Count}");
            Assert(restored.All(p => p.Status != ProcurementStatus.Merged), "and are active again");
            Assert(restored.All(p => p.ParentPrId == null), "with no parent left behind");
        }

        private static async Task SharedRfqFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var a = NewPr("shared-a", (Brush, 12m));
            var b = NewPr("shared-b", (Gasket, 4m));
            await repo.SaveAsync(a);
            await repo.SaveAsync(b);

            var template = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                RfqNo = _marker + "-shared",
                Vendor = _marker + "-shared-vendor",
                Currency = "AED",
                QuoteAmount = 500m,
                VatType = "5%"
            };

            var batchItems = new List<RfqItem>
            {
                new() { Id = Guid.NewGuid(), PrItemId = a.Items[0].Id, ItemName = Brush, Quantity = 12m, Unit = "NOS", IsQuoted = true, QuotedUnitPrice = 10m, Notes = a.PrNo },
                new() { Id = Guid.NewGuid(), PrItemId = b.Items[0].Id, ItemName = Gasket, Quantity = 4m, Unit = "NOS", IsQuoted = true, QuotedUnitPrice = 25m, Notes = b.PrNo },
            };

            await repo.CreateBatchRfqAsync(new List<PurchaseRequisition> { a, b }, template, batchItems);
            await AssertDerivedDataFreshAsync(db, "after a shared RFQ");

            var readA = await Reload(repo, a.Id);
            var readB = await Reload(repo, b.Id);

            Assert(readA.Rfqs.Count == 1 && readB.Rfqs.Count == 1, "each PR gets its own row of the shared quote");
            Assert(readA.Rfqs[0].Id != readB.Rfqs[0].Id, "they are separate rows, so an edit to one cannot touch the other");
            Assert(readA.Rfqs[0].IsSharedRfq && readB.Rfqs[0].IsSharedRfq, "both are marked shared");
            Assert(readA.Rfqs[0].Items.All(i => i.PrItemId.HasValue), "and their lines carry links");
            Assert(readA.Rfqs[0].Items.All(i => readA.Items.Any(p => p.Id == i.PrItemId)),
                "each line links to a line on its OWN requisition, never the other PR's");

            readA.NotifyHierarchyChanged();
            Assert(readA.Rfqs[0].PrLinesCovered == 1 && readA.Rfqs[0].PrLineCount == 1,
                "coverage is measured against that PR alone");
        }

        private static async Task CombinedPoFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var a = NewPr("combined-a", (Brush, 12m));
            var b = NewPr("combined-b", (Gasket, 4m));
            await repo.SaveAsync(a);
            await repo.SaveAsync(b);

            foreach (var (pr, price) in new[] { (a, 10m), (b, 25m) })
            {
                var rfq = NewRfq(pr, "combined-vendor");
                rfq.Items.Add(NewRfqLine(pr.Items[0], price));
                await repo.SaveRfqAsync(rfq);
            }

            var readA = await Reload(repo, a.Id);
            var readB = await Reload(repo, b.Id);

            var template = new PurchaseOrder
            {
                PoNo = _marker + "-combined-po",
                Vendor = _marker + "-combined-vendor",
                Value = readA.Rfqs[0].TotalLandedCost + readB.Rfqs[0].TotalLandedCost,
                Status = PoStatus.Raised,
                Currency = "AED",
                Date = DateTime.Today
            };

            await repo.CreateBatchPoAsync(new List<PurchaseRequisition> { readA, readB }, template);
            await AssertDerivedDataFreshAsync(db, "after a combined PO");

            var afterA = await Reload(repo, a.Id);
            var afterB = await Reload(repo, b.Id);

            Assert(afterA.Pos.Count == 1 && afterB.Pos.Count == 1, "each PR gets its share of the combined order");
            Assert(afterA.Pos[0].IsCombinedPo, "marked as combined");
            Assert(afterA.Pos[0].Items.Count == 1,
                $"and it records its line items - the whole point of the round-1 fix; got {afterA.Pos[0].Items.Count}");
            Assert(afterA.Pos[0].Items[0].PrItemId == afterA.Items[0].Id, "linked to the requisition line");

            afterA.CalculateItemFulfillments();
            Assert(afterA.Items[0].OrderedQuantity == 12m,
                $"so fulfilment counts it instead of reading Unordered for ever; got {afterA.Items[0].OrderedQuantity}");
            Assert(afterA.IsPoFullyOrdered, "and the PR reads complete");
        }

        private static async Task QuoteSyncFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var model = HostServices?.GetService<PrListPageModel>();
            if (model == null)
            {
                Failures.Add("SKIPPED 09: the PR board page model was not available from the container.");
                return;
            }

            var pr = NewPr("sync", (Brush, 12m));
            await repo.SaveAsync(pr);

            // One open quote (no order against it) and one that has already been ordered from.
            var open = NewRfq(pr, "open-vendor");
            open.Items.Add(NewRfqLine(pr.Items[0], price: 15m));
            await repo.SaveRfqAsync(open);

            var ordered = NewRfq(pr, "ordered-vendor");
            ordered.Items.Add(NewRfqLine(pr.Items[0], price: 14m));
            await repo.SaveRfqAsync(ordered);

            var mid = await Reload(repo, pr.Id);
            var orderedRfq = mid.Rfqs.Single(r => r.Vendor.Contains("ordered-vendor", StringComparison.Ordinal));
            var po = NewPo(mid, orderedRfq, "PO-SYNC");
            po.Items.Add(NewPoLine(mid.Items[0], 12m, 14m));
            await repo.SavePoAsync(po);

            var live = await Reload(repo, pr.Id);

            // What the edit modal would produce: the brush renamed and re-quantified, plus a new line.
            var edited = live.Items.Select(i => new PrItem
            {
                Id = i.Id,
                PrId = i.PrId,
                ItemName = Brush + " (RENAMED)",
                Quantity = 20m,
                Unit = i.Unit,
                SortOrder = i.SortOrder
            }).ToList();
            edited.Add(new PrItem { Id = Guid.NewGuid(), PrId = live.Id, ItemName = _marker + "-new-line", Quantity = 5m, Unit = "NOS", SortOrder = 1 });

            // Same order the modal uses: the requisition is written first so the new line exists for
            // the quote line that will point at it, and the pre-edit lines are handed to the sync.
            var preEdit = live.Items.ToList();
            live.Items = new ObservableCollection<PrItem>(edited);
            await repo.SaveAsync(live);
            var summary = await model.RunQuoteSyncForTestAsync(live, preEdit, edited);
            await AssertDerivedDataFreshAsync(db, "after syncing a PR edit into its quotes");

            var after = await Reload(repo, pr.Id);
            var openAfter = after.Rfqs.Single(r => r.Vendor.Contains("open-vendor", StringComparison.Ordinal));
            var orderedAfter = after.Rfqs.Single(r => r.Vendor.Contains("ordered-vendor", StringComparison.Ordinal));

            Assert(openAfter.Items.Count == 2,
                $"the new PR line was added to the open quote; got {openAfter.Items.Count} lines");
            Assert(openAfter.Items.Any(i => i.ItemName.EndsWith("-new-line", StringComparison.Ordinal)), "as an actual line");
            var addedLine = openAfter.Items.Single(i => i.ItemName.EndsWith("-new-line", StringComparison.Ordinal));
            Assert(addedLine.PrItemId.HasValue, "correctly linked");
            Assert(!addedLine.QuotedUnitPrice.HasValue, "and unpriced, so no money moved");

            var renamedInOpen = openAfter.Items.Single(i => i.PrItemId == pr.Items[0].Id);
            Assert(renamedInOpen.ItemName.EndsWith("(RENAMED)", StringComparison.Ordinal),
                $"the rename reached the open quote; got '{renamedInOpen.ItemName}'");

            // The quote line was asking for the requisition's quantity, so the edited quantity
            // follows it. The vendor's rate is theirs and stays put; the total is simply the two
            // multiplied, which is what "only the box I changed moved" means here.
            Assert(renamedInOpen.Quantity == 20m,
                $"and so did the new quantity, which this line was tracking; got {renamedInOpen.Quantity}");
            Assert(renamedInOpen.QuotedUnitPrice == 15m,
                $"while the vendor's rate is untouched; got {renamedInOpen.QuotedUnitPrice}");
            Assert(openAfter.BaseAmount == 20m * 15m, $"so the quote total follows the quantity; got {openAfter.BaseAmount}");

            // An ordered quote takes edited fields too, but never gains or loses a line.
            Assert(orderedAfter.Items.Count == 1, "the ordered quote gained no line - it is a document that went out");
            Assert(orderedAfter.Items[0].ItemName.EndsWith("(RENAMED)", StringComparison.Ordinal),
                "but the corrected description did reach it");

            after.NotifyHierarchyChanged();
            Assert(!string.IsNullOrEmpty(summary), $"and the user is told what moved; got '{summary}'");
            Assert(summary.Contains("added", StringComparison.OrdinalIgnoreCase), $"summary names the addition; got '{summary}'");
        }

        /// <summary>The reported bug, start to finish: price a quote, let the board refresh over the
        /// open requisition (a header-only read, which carries quotes but not their lines), then edit
        /// the requisition. The refresh used to empty the quote in memory and the edit then deleted
        /// the vendor's priced rows from the database, leaving the stored total behind to hide it.
        /// Also covers the other half: the requisition's estimated price reaching the quote.</summary>
        private static async Task RefreshThenEditKeepsPricesAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var model = HostServices?.GetService<PrListPageModel>();
            if (model == null)
            {
                Failures.Add("SKIPPED 09b: the PR board page model was not available from the container.");
                return;
            }

            var pr = NewPr("refresh", (Brush, 10m));
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "refresh-vendor");
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 25m));
            await repo.SaveRfqAsync(rfq);

            // The open requisition, lines and all - what the user has on screen.
            var live = await Reload(repo, pr.Id);
            Assert(live.Rfqs.Single().Items.Count == 1, "the quote starts with its priced line");

            // The board's own refresh: a header-only page read merged onto that live instance.
            var page = await repo.GetPageAsync(new PrQuery(_marker, null, false, false, false, 10, 5, 0, 50));
            var shallow = page.Rows.FirstOrDefault(r => r.Id == pr.Id);
            Assert(shallow != null, "the board refresh returns the requisition");
            if (shallow == null) return;
            Assert(shallow.Rfqs.Single().Items.Count == 0, "and carries its quote without lines, as designed");
            live.MergeFrom(shallow);

            Assert(live.Rfqs.Single().Items.Count == 1,
                $"the refresh leaves the quote's priced line in place; got {live.Rfqs.Single().Items.Count}");

            // Now the edit the user makes: filling in the historical price they forgot, nothing else.
            var edited = live.Items.Select(i => new PrItem
            {
                Id = i.Id,
                PrId = i.PrId,
                ItemName = i.ItemName,
                Quantity = i.Quantity,
                Unit = i.Unit,
                EstimatedUnitPrice = 22m,
                SortOrder = i.SortOrder
            }).ToList();

            var preEdit = live.Items.ToList();
            live.Items = new ObservableCollection<PrItem>(edited);
            await repo.SaveAsync(live);
            await model.RunQuoteSyncForTestAsync(live, preEdit, edited);
            await AssertDerivedDataFreshAsync(db, "after a refresh-then-edit save");

            var after = await Reload(repo, pr.Id);
            var quote = after.Rfqs.Single();

            Assert(quote.Items.Count == 1, $"the quote still has exactly its one line; got {quote.Items.Count}");
            Assert(quote.Items[0].QuotedUnitPrice == 25m,
                $"and the vendor's unit price survived the edit; got {quote.Items[0].QuotedUnitPrice}");
            Assert(quote.Items[0].IsQuoted, "and the line is still quoted");
            Assert(quote.BaseAmount == 10m * 25m, $"so the quote total still adds up from its lines; got {quote.BaseAmount}");
            Assert(quote.Items[0].LastPrice == 22m,
                $"the requisition's estimated price reached the quote's last price; got {quote.Items[0].LastPrice}");
            Assert(quote.Items[0].Quantity == 10m,
                $"and nothing else on the line moved; got quantity {quote.Items[0].Quantity}");
        }

        /// <summary>One edit travels one hop: a changed quoted rate reaches the order raised from that
        /// quote, and only that field. An order line carrying its own quantity (a part order) keeps
        /// it. Nothing travels back up - the requisition's own figures are untouched throughout.</summary>
        private static async Task FieldLevelDownstreamSyncAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var model = HostServices?.GetService<PrListPageModel>();
            if (model == null)
            {
                Failures.Add("SKIPPED 09c: the PR board page model was not available from the container.");
                return;
            }

            var pr = NewPr("downstream", (Brush, 100m));
            pr.Items[0].EstimatedUnitPrice = 9m;
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "down-vendor");
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 50m));
            await repo.SaveRfqAsync(rfq);

            var mid = await Reload(repo, pr.Id);
            var quote = mid.Rfqs.Single();

            // A part order: 40 of the 100 quoted, at the quoted rate.
            var po = NewPo(mid, quote, "PO-DOWN");
            var poLine = NewPoLine(mid.Items[0], 40m, 50m);
            poLine.RfqItemId = quote.Items[0].Id;
            po.Items.Add(poLine);
            await repo.SavePoAsync(po);

            // A second order taking the whole quoted quantity, so both rules are exercised.
            var poFull = NewPo(mid, quote, "PO-DOWN-FULL");
            var fullLine = NewPoLine(mid.Items[0], 100m, 50m);
            fullLine.RfqItemId = quote.Items[0].Id;
            poFull.Items.Add(fullLine);
            await repo.SavePoAsync(poFull);

            var live = await Reload(repo, pr.Id);
            var liveQuote = live.Rfqs.Single();
            var preEdit = liveQuote.Items.Select(i => new RfqItem
            {
                Id = i.Id,
                ItemName = i.ItemName,
                Quantity = i.Quantity,
                Unit = i.Unit,
                QuotedUnitPrice = i.QuotedUnitPrice,
                Discount = i.Discount
            }).ToList();

            // The vendor revises their rate. Nothing else on the quote is touched.
            liveQuote.Items[0].QuotedUnitPrice = 55m;
            await repo.SaveRfqAsync(liveQuote);
            var summary = await model.RunOrderSyncForTestAsync(live, liveQuote, preEdit);
            await AssertDerivedDataFreshAsync(db, "after syncing a quote edit into its orders");

            var after = await Reload(repo, pr.Id);
            var partOrder = after.Pos.Single(p => p.PoNo.EndsWith("PO-DOWN", StringComparison.Ordinal));
            var fullOrder = after.Pos.Single(p => p.PoNo.EndsWith("PO-DOWN-FULL", StringComparison.Ordinal));

            Assert(partOrder.Items[0].UnitPrice == 55m,
                $"the new rate reached the part order; got {partOrder.Items[0].UnitPrice}");
            Assert(partOrder.Items[0].Quantity == 40m,
                $"but the part order kept its own quantity; got {partOrder.Items[0].Quantity}");
            Assert(partOrder.Value == 40m * 55m * 1.05m,
                $"and its value was rebuilt from its own lines; got {partOrder.Value}");
            Assert(fullOrder.Items[0].UnitPrice == 55m && fullOrder.Items[0].Quantity == 100m,
                "the full order followed the rate too, on the quantity it always had");

            Assert(after.Items[0].EstimatedUnitPrice == 9m,
                $"the requisition's own estimated price is untouched; got {after.Items[0].EstimatedUnitPrice}");
            Assert(after.Items[0].Quantity == 100m, "and so is its quantity - nothing travels upward");
            Assert(!string.IsNullOrEmpty(summary), $"and the user is told what moved; got '{summary}'");
        }

        /// <summary>A vendor who replies "Regret" instead of a price. The words are kept on the line
        /// and survive a save and reload; the price stays empty, so the quote's own total, and every
        /// total built from it, counts only the lines that really carry money.</summary>
        private static async Task PriceNoteFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var pr = NewPr("regret", (Brush, 10m), (Gasket, 4m));
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "regret-vendor");
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 30m));
            rfq.Items.Add(NewRfqLine(pr.Items[1], price: 0m));
            await repo.SaveRfqAsync(rfq);

            var live = await Reload(repo, pr.Id);
            var quote = live.Rfqs.Single();
            var second = quote.Items.Single(i => i.PrItemId == pr.Items[1].Id);

            // Typed into the Unit Rate box: not a number, so it is kept as the vendor's words.
            second.QuotedUnitPriceText = "Regret - cannot supply";
            Assert(!second.QuotedUnitPrice.HasValue, "words in the rate box leave the price empty");
            Assert(second.PriceNote == "Regret - cannot supply", $"and are kept verbatim; got '{second.PriceNote}'");
            Assert(second.LineTotal == 0m, $"so the line is worth nothing; got {second.LineTotal}");

            var expectedBase = 10m * 30m;
            Assert(quote.BaseAmount == expectedBase, $"and the quote total counts only the priced line; got {quote.BaseAmount}");

            await repo.SaveRfqAsync(quote);
            await AssertDerivedDataFreshAsync(db, "after saving a quote line with a note instead of a price");

            var after = await Reload(repo, pr.Id);
            var reloaded = after.Rfqs.Single().Items.Single(i => i.PrItemId == pr.Items[1].Id);
            Assert(reloaded.PriceNote == "Regret - cannot supply", $"the note survives a save and reload; got '{reloaded.PriceNote}'");
            Assert(!reloaded.QuotedUnitPrice.HasValue, "and the price is still empty");
            Assert(after.Rfqs.Single().BaseAmount == expectedBase, "and the quote total is unchanged by it");

            // A number typed over the note is a price again, and the words go.
            reloaded.QuotedUnitPriceText = "12.5";
            Assert(reloaded.QuotedUnitPrice == 12.5m, $"a number types back over the note; got {reloaded.QuotedUnitPrice}");
            Assert(string.IsNullOrEmpty(reloaded.PriceNote), "and clears it");

            // And clearing the box empties both.
            reloaded.QuotedUnitPriceText = "";
            Assert(!reloaded.QuotedUnitPrice.HasValue && string.IsNullOrEmpty(reloaded.PriceNote), "clearing the box empties both");

            // A note typed over a price that has already been ordered must not empty the order.
            var model = HostServices?.GetService<PrListPageModel>();
            if (model == null)
            {
                Failures.Add("SKIPPED 09d (order half): the PR board page model was not available.");
                return;
            }

            var ordered = await Reload(repo, pr.Id);
            var orderedQuote = ordered.Rfqs.Single();
            var pricedLine = orderedQuote.Items.Single(i => i.PrItemId == pr.Items[0].Id);

            var po = NewPo(ordered, orderedQuote, "PO-REGRET");
            var poLine = NewPoLine(ordered.Items[0], 10m, 30m);
            poLine.RfqItemId = pricedLine.Id;
            po.Items.Add(poLine);
            po.BaseAmount = 10m * 30m;
            po.Value = 10m * 30m * 1.05m;      // as the PO wizard would have written it
            await repo.SavePoAsync(po);

            var live2 = await Reload(repo, pr.Id);
            var quote2 = live2.Rfqs.Single();
            var line2 = quote2.Items.Single(i => i.Id == pricedLine.Id);
            var pre = new List<RfqItem> { new() { Id = line2.Id, ItemName = line2.ItemName, Quantity = line2.Quantity,
                                                 Unit = line2.Unit, QuotedUnitPrice = line2.QuotedUnitPrice, Discount = line2.Discount } };

            line2.QuotedUnitPriceText = "Regret - withdrawn";
            await repo.SaveRfqAsync(quote2);
            await model.RunOrderSyncForTestAsync(live2, quote2, pre);

            var afterOrder = (await Reload(repo, pr.Id)).Pos.Single(p => p.PoNo.EndsWith("PO-REGRET", StringComparison.Ordinal));
            Assert(afterOrder.Items[0].UnitPrice == 30m,
                $"an order keeps its rate when the quote's price becomes words; got {afterOrder.Items[0].UnitPrice}");
            Assert(afterOrder.Value == 10m * 30m * 1.05m, $"and its value is untouched; got {afterOrder.Value}");
        }

        /// <summary>A historical price is not always in the RFQ's own currency - a past purchase in
        /// USD compared against a fresh AED quote. Covers the smart-parse box (typing a currency
        /// alongside the number, or leaving it out and keeping what was already there), the sync from
        /// a requisition's estimate into its open quotes, and that a PR with items in two different
        /// currencies keeps each one distinct rather than collapsing to one guess.</summary>
        private static async Task HistoricalPriceCurrencyFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            SmartPriceParser.Demo();

            // The smart-parse box itself: a currency typed with the number is kept; a bare number
            // leaves whatever currency the row already had.
            var probe = new PrItem { Id = Guid.NewGuid(), ItemName = "probe" };
            probe.EstimatedPriceText = "86.75 usd";
            Assert(probe.EstimatedUnitPrice == 86.75m && probe.EstimatedCurrency == "USD",
                $"typing a currency with the price sets both; got {probe.EstimatedUnitPrice} {probe.EstimatedCurrency}");
            probe.EstimatedPriceText = "90";
            Assert(probe.EstimatedUnitPrice == 90m && probe.EstimatedCurrency == "USD",
                $"a bare number afterwards keeps the currency already there; got {probe.EstimatedUnitPrice} {probe.EstimatedCurrency}");

            // The box shows the currency back, capitalised, so reopening it later still says what
            // it was priced in - not just a bare number that could be anything by then.
            Assert(probe.EstimatedPriceText == "90 USD",
                $"the box displays the currency alongside the number; got '{probe.EstimatedPriceText}'");

            // A PR with two items priced in two different currencies - the case the bug report was
            // actually about: one column that guessed a single currency for the whole sheet.
            var pr = NewPr("fx", (Brush, 10m), (Gasket, 5m));
            pr.Items[0].EstimatedUnitPrice = 45m;
            pr.Items[0].EstimatedCurrency = "AED";
            pr.Items[1].EstimatedUnitPrice = 86.75m;
            pr.Items[1].EstimatedCurrency = "USD";
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "fx-vendor");
            var line0 = NewRfqLine(pr.Items[0], price: 50m);
            line0.LastPrice = pr.Items[0].EstimatedUnitPrice;
            line0.LastPriceCurrency = pr.Items[0].EstimatedCurrency;
            var line1 = NewRfqLine(pr.Items[1], price: 340m);
            line1.LastPrice = pr.Items[1].EstimatedUnitPrice;
            line1.LastPriceCurrency = pr.Items[1].EstimatedCurrency;
            rfq.Items.Add(line0);
            rfq.Items.Add(line1);
            await repo.SaveRfqAsync(rfq);

            var live = await Reload(repo, pr.Id);
            var quote = live.Rfqs.Single();
            Assert(quote.Items.Single(i => i.PrItemId == pr.Items[0].Id).LastPriceCurrency == "AED",
                "the AED item's historical currency survives a save and reload");
            Assert(quote.Items.Single(i => i.PrItemId == pr.Items[1].Id).LastPriceCurrency == "USD",
                "and the USD item's stays distinct, not collapsed to the RFQ's own AED");

            // Editing the requisition's estimate pushes the currency into the open quote too, the
            // same hop QuoteSyncFlowAsync already checks for the bare number.
            var edited = live.Items.Select(i => new PrItem
            {
                Id = i.Id, PrId = i.PrId, ItemName = i.ItemName, Quantity = i.Quantity, Unit = i.Unit,
                EstimatedUnitPrice = i.Id == pr.Items[1].Id ? 92m : i.EstimatedUnitPrice,
                EstimatedCurrency = i.Id == pr.Items[1].Id ? "EUR" : i.EstimatedCurrency,
                SortOrder = i.SortOrder
            }).ToList();
            var preEdit = live.Items.ToList();
            var model = HostServices?.GetService<PrListPageModel>();
            if (model == null)
            {
                Failures.Add("SKIPPED 09e (sync half): the PR board page model was not available.");
                return;
            }
            live.Items = new ObservableCollection<PrItem>(edited);
            await repo.SaveAsync(live);
            await model.RunQuoteSyncForTestAsync(live, preEdit, edited);

            var after = await Reload(repo, pr.Id);
            var afterLine1 = after.Rfqs.Single().Items.Single(i => i.PrItemId == pr.Items[1].Id);
            Assert(afterLine1.LastPriceCurrency == "EUR",
                $"a currency change to the requisition's estimate reaches the open quote; got {afterLine1.LastPriceCurrency}");
        }

        private static async Task GuardDecisionsAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var pr = NewPr("guards", (Brush, 12m), (Gasket, 4m));
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "guard-vendor");
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 15m));
            rfq.Items.Add(NewRfqLine(pr.Items[1], price: 25m));
            await repo.SaveRfqAsync(rfq);

            var mid = await Reload(repo, pr.Id);
            var po = NewPo(mid, mid.Rfqs.Single(), "PO-GUARD");
            po.Items.Add(NewPoLine(mid.Items[0], 12m, 15m));
            await repo.SavePoAsync(po);

            var live = await Reload(repo, pr.Id);
            var orderedLine = live.Items.Single(i => i.ItemName == Brush);
            var quotedOnly = live.Items.Single(i => i.ItemName == Gasket);

            // These are the decisions the confirmation dialogs are built on. The dialogs themselves
            // need someone to answer them, so the check asserts what they would find.
            Assert(PrListPageModel.OrderedLineCountForTest(live, orderedLine) == 1,
                "the guard sees that the brush line is on an order, so removing it is refused");
            Assert(PrListPageModel.OrderedLineCountForTest(live, quotedOnly) == 0,
                "and that the gasket line is not");
            Assert(PrListPageModel.QuotedLineCountForTest(live, quotedOnly) == 1,
                "but is priced by one vendor, so removing it asks first");
            Assert(PrListPageModel.QuotedLineCountForTest(live, orderedLine) == 1,
                "the brush is priced too");

            // The other half of the guard: the PO delete warning has to be able to name the order.
            Assert(live.Pos.Count == 1 && !string.IsNullOrWhiteSpace(live.Pos[0].PoNo),
                "the PR knows the PO number the warnings quote back at you");

            await AssertDerivedDataFreshAsync(db, "after the guard fixtures");
        }

        private static async Task DeleteFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var pr = NewPr("delete", (Brush, 12m));
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "delete-vendor");
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 15m));
            await repo.SaveRfqAsync(rfq);

            var mid = await Reload(repo, pr.Id);
            var po = NewPo(mid, mid.Rfqs.Single(), "PO-DEL");
            po.Items.Add(NewPoLine(mid.Items[0], 12m, 15m));
            await repo.SavePoAsync(po);
            await AssertDerivedDataFreshAsync(db, "before deleting");

            await repo.DeletePoAsync(po.Id);
            await AssertDerivedDataFreshAsync(db, "after deleting a PO");
            var afterPo = await Reload(repo, pr.Id);
            Assert(afterPo.Pos.Count == 0, "the PO is gone");
            afterPo.CalculateItemFulfillments();
            Assert(afterPo.Items[0].OrderedQuantity == 0m,
                $"and its quantity stops counting; got {afterPo.Items[0].OrderedQuantity}");

            await repo.DeleteRfqAsync(rfq.Id);
            await AssertDerivedDataFreshAsync(db, "after deleting an RFQ");
            var afterRfq = await Reload(repo, pr.Id);
            Assert(afterRfq.Rfqs.Count == 0, "the quote is gone");

            await repo.DeleteAsync(pr.Id);
            Created.Remove(pr.Id);
            await AssertDerivedDataFreshAsync(db, "after deleting a PR");
            var gone = await repo.GetByIdsAsync(new[] { pr.Id });
            Assert(gone.Count == 0, "and so is the requisition");
        }

        /// <summary>The Undo window against the real repositories: whatever is waiting to be deleted is
        /// missing from every read a screen uses (board page and count, a reloaded PR's quotes and
        /// orders, the task list and its sub-tasks, the note list), comes back on Undo, and is really
        /// gone - cascades included - once the delete is final.</summary>
        private static async Task UndoDeleteFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var todoRepo = new TodoRepository(db);
            var noteRepo = new NoteRepository(db);
            var journalDir = Path.Combine(Path.GetTempPath(), "procure-undo-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journalDir);
            var undo = new Services.UndoDeleteService(Services.UndoDeleteService.RepositoryDelete(repo, todoRepo, noteRepo),
                () => journalDir, new Abstractions.HeadlessUiDispatcher(), null);
            try
            {
                var pr = NewPr("undo", (Brush, 12m));
                await repo.SaveAsync(pr);
                var rfq = NewRfq(pr, "undo-vendor");
                rfq.Items.Add(NewRfqLine(pr.Items[0], price: 15m));
                await repo.SaveRfqAsync(rfq);
                var mid = await Reload(repo, pr.Id);
                var po = NewPo(mid, mid.Rfqs.Single(), "PO-UNDO");
                po.Items.Add(NewPoLine(mid.Items[0], 12m, 15m));
                await repo.SavePoAsync(po);

                // RFQ: hidden on reload, back on Undo.
                await undo.StartAsync("rfq", new[] { new PendingDeleteItem(DeleteKind.Rfq, rfq.Id) }, () => { });
                Assert((await Reload(repo, pr.Id)).Rfqs.Count == 0, "undo: a waiting RFQ is left off the reloaded PR");
                undo.Undo();
                Assert((await Reload(repo, pr.Id)).Rfqs.Any(r => r.Id == rfq.Id), "undo: Undo brings the RFQ back");

                // PO: hidden, then really deleted when the window closes.
                await undo.StartAsync("po", new[] { new PendingDeleteItem(DeleteKind.Po, po.Id) }, () => { });
                Assert((await Reload(repo, pr.Id)).Pos.Count == 0, "undo: a waiting PO is left off the reloaded PR");
                await undo.CommitNowAsync();
                Assert(PendingDeleteFilter.IsEmpty, "undo: nothing stays hidden once the delete is final");
                Assert((await Reload(repo, pr.Id)).Pos.Count == 0, "undo: the PO is gone for good");
                await AssertDerivedDataFreshAsync(db, "after an undo-window PO delete");

                // PR: off the board page and its count, back on Undo, gone with its RFQ once final.
                var byNo = new PrQuery(pr.PrNo, null, false, false, false, 10, 5, 0, 10);
                Assert((await repo.GetPageAsync(byNo)).TotalCount == 1, "undo: the PR is on the board to start with");
                await undo.StartAsync("pr", new[] { new PendingDeleteItem(DeleteKind.Pr, pr.Id) }, () => { });
                var hidden = await repo.GetPageAsync(byNo);
                Assert(hidden.TotalCount == 0 && hidden.Rows.Count == 0, "undo: a waiting PR is off the board and its count");
                undo.Undo();
                Assert((await repo.GetPageAsync(byNo)).TotalCount == 1, "undo: Undo puts the PR back on the board");
                await undo.StartAsync("pr", new[] { new PendingDeleteItem(DeleteKind.Pr, pr.Id) }, () => { });
                await undo.CommitNowAsync();
                Created.Remove(pr.Id);
                Assert((await repo.GetByIdsAsync(new[] { pr.Id })).Count == 0, "undo: the PR is deleted once final");
                await AssertDerivedDataFreshAsync(db, "after an undo-window PR delete");

                // Task with a sub-task: both hidden, both back, both gone (cascade).
                var now = DateTime.Now;
                var task = new TodoTask { Title = _marker + "-undo-task", CreatedAt = now, UpdatedAt = now };
                var sub = new TodoTask { Title = _marker + "-undo-sub", ParentId = task.Id, CreatedAt = now, UpdatedAt = now };
                await todoRepo.UpsertAsync(task);
                await todoRepo.UpsertAsync(sub);
                await undo.StartAsync("task", new[] { new PendingDeleteItem(DeleteKind.Task, task.Id) }, () => { });
                var tasks = await todoRepo.GetAllAsync();
                Assert(!tasks.Any(t => t.Id == task.Id || t.Id == sub.Id), "undo: a waiting task and its sub-task are off the list");
                undo.Undo();
                tasks = await todoRepo.GetAllAsync();
                Assert(tasks.Any(t => t.Id == task.Id) && tasks.Any(t => t.Id == sub.Id), "undo: Undo brings back the task and its sub-task");
                await undo.StartAsync("task", new[] { new PendingDeleteItem(DeleteKind.Task, task.Id) }, () => { });
                await undo.CommitNowAsync();
                tasks = await todoRepo.GetAllAsync();
                Assert(!tasks.Any(t => t.Id == task.Id || t.Id == sub.Id), "undo: the task and its sub-task are deleted once final");

                // Note.
                var note = new Note { Title = _marker + "-undo-note", CreatedAt = now, UpdatedAt = now };
                await noteRepo.UpsertAsync(note, "body");
                await undo.StartAsync("note", new[] { new PendingDeleteItem(DeleteKind.Note, note.Id) }, () => { });
                Assert(!(await noteRepo.GetListAsync()).Any(n => n.Id == note.Id), "undo: a waiting note is off the list");
                undo.Undo();
                Assert((await noteRepo.GetListAsync()).Any(n => n.Id == note.Id), "undo: Undo brings the note back");
                await undo.StartAsync("note", new[] { new PendingDeleteItem(DeleteKind.Note, note.Id) }, () => { });
                await undo.CommitNowAsync();
                Assert(await noteRepo.GetAsync(note.Id) is null, "undo: the note is deleted once final");
            }
            finally
            {
                await undo.CommitNowAsync();
                undo.Undo();
                try { Directory.Delete(journalDir, recursive: true); } catch { }
            }
        }

        /// <summary>The Vendor box: the suggestion list follows every RFQ write (spelling and case
        /// merged, newest terms win, a delete falls back, a cascade removes), typed wildcards are
        /// literal, and picking a vendor fills only the terms nobody changed - and none when editing.</summary>
        private static async Task VendorSuggestionFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var pr = NewPr("vendor", (Brush, 5m));
            await repo.SaveAsync(pr);
            var name = $"{_marker} Zeta 100% Supplies";

            var first = NewRfq(pr, "v1");
            first.Vendor = "  " + name.ToUpperInvariant() + " ";
            first.Currency = "USD"; first.PaymentTerms = "60 Days"; first.Incoterms = "FOB"; first.VatType = "RC";
            first.SentDate = DateTime.Today;
            await repo.SaveRfqAsync(first);

            var found = await repo.SearchVendorsAsync(_marker + " zeta");
            Assert(found.Count == 1, $"vendor: a vendor used on an RFQ is suggested; got {found.Count}");
            var v = found.FirstOrDefault() ?? new VendorSuggestion();
            Assert(v.Currency == "USD" && v.PaymentTerms == "60 Days" && v.Incoterms == "FOB" && v.VatType == "RC",
                $"vendor: it carries that RFQ's terms; got {v.Currency}/{v.PaymentTerms}/{v.Incoterms}/{v.VatType}");
            Assert(v.Name == name.ToUpperInvariant(), $"vendor: the name is shown trimmed; got '{v.Name}'");
            Assert((await repo.SearchVendorsAsync(_marker + " zeta 100%")).Count == 1, "vendor: a typed % matches itself");
            Assert((await repo.SearchVendorsAsync(_marker + "%supplies")).Count == 0, "vendor: a typed % is not a wildcard");

            var second = NewRfq(pr, "v2");
            second.Vendor = name;   // same vendor, different case
            second.Currency = "EUR";
            second.SentDate = DateTime.Today.AddDays(1);
            await repo.SaveRfqAsync(second);
            found = await repo.SearchVendorsAsync(_marker + " zeta");
            Assert(found.Count == 1, $"vendor: different case and spacing is one vendor; got {found.Count}");
            Assert(found.FirstOrDefault()?.Currency == "EUR" && found.FirstOrDefault()?.Name == name,
                "vendor: the newest RFQ's spelling and terms win");

            await repo.DeleteRfqAsync(second.Id);
            Assert((await repo.SearchVendorsAsync(_marker + " zeta")).FirstOrDefault()?.Currency == "USD",
                "vendor: deleting the newest RFQ falls back to the one before");

            var model = HostServices?.GetService<PrListPageModel>();
            if (model == null)
            {
                Failures.Add("SKIPPED 15 (form part): the PR board page model was not available from the container.");
            }
            else
            {
                model.IsEditingRfq = false;
                model.NewRfqCurrency = "AED"; model.NewRfqPaymentTerms = string.Empty; model.NewRfqIncoterms = "DDP"; model.NewRfqVatType = "5%";
                model.ResetRfqTermsTouched();          // as a freshly opened form
                model.NewRfqPaymentTerms = "Advance";  // the user types their own terms
                model.ApplyRfqVendor(v);
                Assert(model.NewRfqVendor == v.Name, "vendor: picking a suggestion fills the name");
                Assert(model.NewRfqCurrency == "USD" && model.NewRfqIncoterms == "FOB" && model.NewRfqVatType == "RC",
                    $"vendor: and the terms left at their defaults; got {model.NewRfqCurrency}/{model.NewRfqIncoterms}/{model.NewRfqVatType}");
                Assert(model.NewRfqPaymentTerms == "Advance", "vendor: but never terms the user typed");

                model.IsEditingRfq = true;
                model.NewRfqCurrency = "AED";
                model.ApplyRfqVendor(v);
                Assert(model.NewRfqCurrency == "AED", "vendor: editing an existing quote keeps its own terms");
                model.IsEditingRfq = false;

                var superseded = model.FindVendorsAsync(_marker + " ze");
                var latest = model.FindVendorsAsync(_marker + " zet");
                Assert(await superseded is null, "vendor: a keystroke overtaken by the next one returns nothing");
                Assert((await latest)?.Count == 1, "vendor: the last keystroke's search returns the vendor");
                Assert((await model.FindVendorsAsync("z"))?.Count == 0, "vendor: one character suggests nothing");
            }

            await repo.DeleteAsync(pr.Id);
            Created.Remove(pr.Id);
            Assert((await repo.SearchVendorsAsync(_marker + " zeta")).Count == 0,
                "vendor: once no RFQ or PO names a vendor, it stops being suggested");
            await AssertDerivedDataFreshAsync(db, "after the vendor suggestion checks");
        }

        /// <summary>Last price: the newest priced PO line for an item, by PO date, net of its discount,
        /// matched ignoring case and outer spaces, never from the quote's own PR - and a PO whose date
        /// is changed moves with it. The form fills only empty lines and says where the price came from.</summary>
        private static async Task LastPaidPriceFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var item = $"{_marker} Widget Z";
            var bought = NewPr("lastprice-a", (item, 10m));
            await repo.SaveAsync(bought);
            var quote = NewRfq(bought, "lp");
            quote.Items.Add(NewRfqLine(bought.Items[0], price: 10m));
            await repo.SaveRfqAsync(quote);
            var withQuote = await Reload(repo, bought.Id);

            var older = NewPo(withQuote, withQuote.Rfqs.Single(), "PO-LP-OLD");
            older.Date = DateTime.Today.AddDays(-30);
            older.Items.Add(NewPoLine(withQuote.Items[0], 4m, 10m));
            older.Items[0].Discount = 1m;   // paid 9 a unit
            await repo.SavePoAsync(older);

            var newer = NewPo(withQuote, withQuote.Rfqs.Single(), "PO-LP-NEW");
            newer.Date = DateTime.Today.AddDays(-2);
            newer.Currency = "USD";
            newer.Items.Add(NewPoLine(withQuote.Items[0], 6m, 20m));
            await repo.SavePoAsync(newer);

            var unpriced = NewPo(withQuote, withQuote.Rfqs.Single(), "PO-LP-ZERO");
            unpriced.Date = DateTime.Today;   // newest of all, but no price: never a "last price"
            unpriced.Items.Add(NewPoLine(withQuote.Items[0], 1m, 0m));
            await repo.SavePoAsync(unpriced);

            var other = NewPr("lastprice-b", (item, 3m));
            await repo.SaveAsync(other);

            var found = await repo.GetLastPaidPricesAsync(new[] { "  " + item.ToUpperInvariant() + " " }, new[] { other.Id });
            var paid = found.Values.FirstOrDefault();
            Assert(paid is not null, "last price: an item bought before is found, whatever its case and spacing");
            Assert(paid?.Price == 20m && paid.Currency == "USD" && paid.PoNo == newer.PoNo,
                $"last price: the newest priced PO wins; got {paid?.Price} {paid?.Currency} {paid?.PoNo}");
            Assert(paid?.Source.StartsWith("PO " + newer.PoNo) == true, $"last price: it names its PO; got '{paid?.Source}'");

            Assert((await repo.GetLastPaidPricesAsync(new[] { item }, new[] { bought.Id })).Count == 0,
                "last price: a quote's own PR never supplies its last price");

            // Moving the older PO's date past the newer one makes it the last purchase.
            older.Date = DateTime.Today.AddDays(-1);
            await repo.SavePoAsync(older);
            paid = (await repo.GetLastPaidPricesAsync(new[] { item }, new[] { other.Id })).Values.FirstOrDefault();
            Assert(paid?.Price == 9m && paid.PoNo == older.PoNo,
                $"last price: follows a PO date change, net of the line discount; got {paid?.Price} {paid?.PoNo}");
            await AssertDerivedDataFreshAsync(db, "after the last price checks");

            var model = HostServices?.GetService<PrListPageModel>();
            if (model == null)
            {
                Failures.Add("SKIPPED 16 (form part): the PR board page model was not available from the container.");
            }
            else
            {
                var empty = new RfqItem { ItemName = item };
                var estimated = new RfqItem { ItemName = item, LastPrice = 5m, LastPriceCurrency = "AED" };
                var neverBought = new RfqItem { ItemName = _marker + " never bought" };
                await model.FillLastPricesAsync(new[] { empty, estimated, neverBought }, new[] { other.Id });
                Assert(empty.LastPrice == 9m && empty.LastPriceCurrency == "AED" && empty.HasLastPriceSource,
                    $"last price: an empty line is filled, with its source; got {empty.LastPrice} {empty.LastPriceCurrency} '{empty.LastPriceSource}'");
                Assert(estimated.LastPrice == 5m && !estimated.HasLastPriceSource, "last price: a PR estimate is never replaced");
                Assert(neverBought.LastPrice is null, "last price: an item never bought stays empty");
                empty.LastPriceText = "12";
                Assert(!empty.HasLastPriceSource, "last price: typing a price removes the PO note");

                // The PR side - where the price lives and flows on to every RFQ and the PCR.
                var line = new PrItem { ItemName = item };
                var typed = new PrItem { ItemName = item };
                typed.EstimatedPriceText = "7 usd";
                await model.FillPrItemPricesAsync(new[] { line, typed }, other.Id);
                Assert(line.EstimatedUnitPrice == 9m && line.EstimatedCurrency == "AED" && line.HasEstimatedPriceSource,
                    $"last price: a PR line's empty estimate is filled from the last PO; got {line.EstimatedUnitPrice} '{line.EstimatedPriceSource}'");
                Assert(typed.EstimatedUnitPrice == 7m && typed.EstimatedCurrency == "USD", "last price: a typed PR estimate is never replaced");

                var own = new PrItem { ItemName = item };
                await model.FillPrItemPricesAsync(new[] { own }, bought.Id);
                Assert(own.EstimatedUnitPrice is null, "last price: a PR's own POs are not its last price");

                line.ItemName = _marker + " never bought";
                await model.FillPrItemPricesAsync(new[] { line }, other.Id);
                Assert(line.EstimatedUnitPrice is null && !line.HasEstimatedPriceSource,
                    "last price: renaming a filled line to an item never bought clears the filled price");
                line.ItemName = item;
                await model.FillPrItemPricesAsync(new[] { line }, other.Id);
                Assert(line.EstimatedUnitPrice == 9m, "last price: renaming it back fills it again");
            }

            await repo.DeleteAsync(bought.Id);
            await repo.DeleteAsync(other.Id);
            Created.Remove(bought.Id);
            Created.Remove(other.Id);
            await AssertDerivedDataFreshAsync(db, "after removing the last price PRs");
        }

        /// <summary>The Suppliers &amp; Items page. Same-round head-to-head and the average against the
        /// others in those rounds; "said no"; spend per currency; the items table against the market
        /// at the time; a look-alike item offered as a duplicate, folded in and separated again; notes
        /// on a period; Avoid ranking last; contact details surviving the vendor rows being rebuilt.</summary>
        private static async Task SuppliersFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var suppliers = new SupplierRepository(db);
            var vendor = $"{_marker} Omega Supplies";
            var key = vendor.ToLowerInvariant();
            var item = $"{_marker} Gasket 4-core";
            var itemKey = item.ToLowerInvariant();
            var twin = $"{_marker.ToUpperInvariant()} GASKET 4 CORE";
            var now = MonthIndex.Of(DateTime.Today);
            var aedOnly = new Dictionary<string, decimal> { ["AED"] = 1m };
            var ctx = new PriceContext(now - 11, now, "AED", aedOnly);
            var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

            async Task<PurchaseRequisition> Round(string tag, string itemName, DateTime date, params (string Vendor, decimal? Price)[] quotes)
            {
                var pr = NewPr(tag, (itemName, 10m));
                await repo.SaveAsync(pr);
                foreach (var (who, price) in quotes)
                {
                    var rfq = NewRfq(pr, tag + who.Length);
                    rfq.Vendor = who;
                    rfq.QuoteReceivedDate = date;
                    var line = NewRfqLine(pr.Items[0], price ?? 0m);
                    if (price is null) { line.IsQuoted = false; line.PriceNote = "Regret"; }
                    rfq.Items.Add(line);
                    await repo.SaveRfqAsync(rfq);
                }
                return pr;
            }

            var rival1 = $"{_marker} Rival One";
            var rival2 = $"{_marker} Rival Two";
            // Round 1: Omega 100 against 90 and 110 - not cheapest, level with the others' average.
            var pr1 = await Round("sup1", item, monthStart, (vendor, 100m), (rival1, 90m), (rival2, 110m));
            // Round 2: Omega 80 against 95 - cheapest, 16% below.
            var pr2 = await Round("sup2", item, DateTime.Today, (vendor, 80m), (rival1, 95m));
            // Round 3: Omega said no.
            var pr3 = await Round("sup3", item, DateTime.Today, (vendor, null));

            var live2 = await Reload(repo, pr2.Id);
            var aed = NewPo(live2, live2.Rfqs.First(r => r.Vendor == vendor), "PO-S-AED");
            aed.Value = 400m;
            aed.Items.Add(NewPoLine(live2.Items[0], 5m, 80m));
            await repo.SavePoAsync(aed);
            var usd = NewPo(live2, live2.Rfqs.First(r => r.Vendor == vendor), "PO-S-USD");
            usd.Value = 100m; usd.Currency = "USD";
            await repo.SavePoAsync(usd);
            await AssertDerivedDataFreshAsync(db, "after the suppliers rounds");

            var summary = await suppliers.GetSummaryAsync(key, ctx);
            Assert(summary is not null, "suppliers: a vendor on an RFQ and a PO has a page");
            Assert(summary?.Rounds == 2 && summary.RoundsWon == 1, $"suppliers: cheapest in 1 of 2 same-PR rounds; got {summary?.HeadToHeadText}");
            Assert(summary?.PoCount == 2 && summary.BoughtText.Contains("AED 400") && summary.BoughtText.Contains("USD 100"),
                $"suppliers: bought is converted where there is a rate and shown apart where not; got {summary?.BoughtText} / {summary?.PoCountText}");
            Assert(summary?.Categories.Contains("Seals & gaskets") == true, "suppliers: categories come from the item names");

            var keys = await suppliers.GetVendorItemKeysAsync(key, ctx);
            Assert(keys.SequenceEqual(new[] { itemKey }), $"suppliers: one item quoted; got {string.Join(",", keys)}");
            var row = (await suppliers.GetVendorItemRowsAsync(keys)).Single();
            Assert(row.Name == item, $"suppliers: the items list shows the item's name; got {row.Name}");
            var (chart, others) = await suppliers.GetVendorItemDetailAsync(key, itemKey, ctx);
            Assert(chart.Dots.Count(d => !d.IsPo) == 2 && chart.Dots.Count(d => d.IsPo) == 1 && chart.Dots.All(d => d.Vendor == vendor),
                "suppliers: the row chart shows only this vendor's own quotes and orders");
            Assert(chart.Market.Count == 1 && Math.Abs(chart.Market[0].Value - 95) < 1e-9, "suppliers: the market line is this month's middle quote");
            Assert(others.Count == 2 && others.All(o => o.Vendor != vendor), "suppliers: the others' latest, one each, never the vendor itself");

            // A look-alike spelling is offered as a duplicate of the busier item, folded in, and separated again.
            var pr4 = await Round("sup4", twin, DateTime.Today, (rival1, 97m));
            var (items, _) = await suppliers.GetItemPageAsync(_marker + " gasket", 0, 10);
            Assert(items.Count == 2 && items.Single(i => i.Key == twin.ToLowerInvariant()).TwinKey == itemKey && !items.Single(i => i.Key == itemKey).HasTwin,
                $"items: the look-alike offers the busier item, not the other way round; got {string.Join(" | ", items.Select(i => i.Key + " -> " + i.TwinKey))}");
            await suppliers.TreatAsOneAsync(twin.ToLowerInvariant(), itemKey);
            (items, _) = await suppliers.GetItemPageAsync(_marker + " gasket", 0, 10);
            Assert(items.Count == 1 && items[0].Key == itemKey, "items: once treated as one, only the kept item is listed");
            var detail = await suppliers.GetItemDetailAsync(itemKey, ctx);
            Assert(detail?.Aliases.Count == 1 && detail.HeaderText.Contains("AED 96.00"),
                $"items: the folded-in quote counts towards the market (middle of 100, 90, 110, 80, 95, 97 = 96); got {detail?.HeaderText}");
            Assert(detail?.Suppliers.FirstOrDefault()?.Key == key, "items: the cheapest against the market ranks first");
            await suppliers.SeparateAsync(twin.ToLowerInvariant());
            Assert((await suppliers.GetItemDetailAsync(itemKey, ctx))?.Aliases.Count == 0, "items: separated again");

            // A note on a period shows on the chart and can be taken off again.
            await suppliers.AddItemNoteAsync(itemKey, now, now, "freight");
            detail = await suppliers.GetItemDetailAsync(itemKey, ctx);
            Assert(detail?.Notes.Count == 1 && detail.Chart.Bands.Any(b => b.Note == "freight"), "items: a note is kept and drawn");
            await suppliers.DeleteItemNoteAsync(detail!.Notes[0].Id);
            Assert((await suppliers.GetItemDetailAsync(itemKey, ctx))?.Notes.Count == 0, "items: a note can be removed");

            // Contact details and the tag are the user's, kept apart from the rows the triggers rebuild;
            // a supplier marked Avoid goes to the bottom however cheap.
            await suppliers.SaveContactAsync(key, "  sales@omega.test ", "Priya", "+971 4", "Ask for Priya", SupplierTag.Avoid, null);
            Assert((await suppliers.GetSummaryAsync(key, ctx))?.Categories.Contains("Seals & gaskets") == true,
                "suppliers: saving contact details without touching categories keeps the guess");
            await suppliers.SaveContactAsync(key, "sales@omega.test", "Priya", "+971 4", "Ask for Priya", SupplierTag.Avoid, new[] { "Valves", " Hoses " });
            Assert((await suppliers.GetSummaryAsync(key, ctx))?.Categories.SequenceEqual(new[] { "Valves", "Hoses" }) == true,
                "suppliers: categories the user set replace the guess");
            await suppliers.SaveContactAsync(key, "sales@omega.test", "Priya", "+971 4", "Ask for Priya", SupplierTag.Avoid, null);
            Assert((await suppliers.GetSummaryAsync(key, ctx))?.Categories.SequenceEqual(new[] { "Valves", "Hoses" }) == true,
                "suppliers: a later contact save keeps the user's categories");
            await suppliers.SaveContactAsync(key, "sales@omega.test", "Priya", "+971 4", "Ask for Priya", SupplierTag.Avoid, System.Array.Empty<string>());
            Assert((await suppliers.GetSummaryAsync(key, ctx))?.Categories.Count == 0, "suppliers: removing every category leaves none, not the guess");
            var lost = (await Reload(repo, pr1.Id)).Rfqs.First(r => r.Vendor == vendor);
            lost.PaymentTerms = "90 Days";   // any write rebuilds this vendor's derived rows
            await repo.SaveRfqAsync(lost);
            summary = await suppliers.GetSummaryAsync(key, ctx);
            Assert(summary?.Email == "sales@omega.test" && summary.Person == "Priya" && summary.IsAvoid,
                "suppliers: contact details and tag survive the vendor list being rebuilt");
            Assert((await suppliers.GetItemDetailAsync(itemKey, ctx))?.Suppliers.LastOrDefault()?.Key == key, "items: Avoid ranks last");
            var (tagged, taggedTotal) = await suppliers.GetPageAsync(_marker, SupplierSort.Recent, SupplierTag.Avoid, aedOnly, 0, 10);
            Assert(taggedTotal == 1 && tagged.Single().Key == key && tagged[0].IsAvoid, "suppliers: the Avoid filter");

            // Sort by spend: with USD at 3.6725, 100 USD (367.25) + 400 AED puts it above a 700 AED vendor;
            // without a USD rate it falls below.
            var rival = NewPr("suppliers-rival", (Brush, 1m));
            await repo.SaveAsync(rival);
            var rivalPo = NewPo(rival, NewRfq(rival, "sr"), "PO-S-RIVAL");
            rivalPo.LinkedRfqId = null; rivalPo.Vendor = $"{_marker} Omega Rival"; rivalPo.Value = 700m; rivalPo.Currency = "AED";
            await repo.SavePoAsync(rivalPo);
            var withRate = (await suppliers.GetPageAsync(_marker + " omega", SupplierSort.Spend, "",
                new Dictionary<string, decimal> { ["AED"] = 1m, ["USD"] = 3.6725m }, 0, 10)).Rows;
            var withoutRate = (await suppliers.GetPageAsync(_marker + " omega", SupplierSort.Spend, "", aedOnly, 0, 10)).Rows;
            Assert(withRate.FirstOrDefault()?.Key == key, "suppliers: spend sorts in the local currency at the Settings rates");
            Assert(withoutRate.FirstOrDefault()?.Key != key, "suppliers: a currency without a rate does not count towards the order");

            await AssertDerivedDataFreshAsync(db, "after the suppliers checks");
            foreach (var pr in new[] { pr1, pr2, pr3, pr4, rival })
            {
                await repo.DeleteAsync(pr.Id);
                Created.Remove(pr.Id);
            }
            Assert(await suppliers.GetSummaryAsync(key, ctx) is null, "suppliers: a vendor no RFQ or PO names any more has no page");
            Assert(await suppliers.GetItemDetailAsync(itemKey, ctx) is null, "items: an item no line names any more has no page");
            await AssertDerivedDataFreshAsync(db, "after removing the suppliers PRs");

            using var connection = db.CreateConnection();
            await connection.OpenAsync();
            using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM VendorContact WHERE VendorKey = @K; DELETE FROM ItemAlias WHERE CanonicalKey = @I; DELETE FROM ItemNote WHERE ItemKey = @I;";
            cleanup.Parameters.AddWithValue("@K", key);
            cleanup.Parameters.AddWithValue("@I", itemKey);
            await cleanup.ExecuteNonQueryAsync();
        }

        /// <summary>Two saves landing on the same Raw Material PR at once.
        ///
        /// This is the shape that put "UNIQUE constraint failed: MaterialAggregate.MaterialKey" in
        /// front of the user when they expanded a requisition. The material totals are refreshed as a
        /// DELETE followed by an INSERT, and most callers run that pair with no transaction around
        /// it; two of them overlapping interleaves as delete, delete, insert, insert, and the second
        /// insert collided. The refresh is an upsert now, so the second write updates the row instead
        /// of failing - and the row still has to end up correct, which is the other half of this.</summary>
        private static async Task ConcurrentMaterialSavesAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var pr = NewPr("material-race", (Gypsum, 500m));
            pr.PrType = ProcurementPrType.RawMaterial;
            await repo.SaveAsync(pr);

            var rfq = NewRfq(pr, "race-vendor");
            rfq.Items.Add(NewRfqLine(pr.Items[0], price: 100m));
            await repo.SaveRfqAsync(rfq);

            var mid = await Reload(repo, pr.Id);
            var po = NewPo(mid, mid.Rfqs.Single(), "PO-RACE");
            po.Items.Add(NewPoLine(mid.Items[0], 500m, 100m));
            await repo.SavePoAsync(po);
            await AssertDerivedDataFreshAsync(db, "after a Raw Material PO");

            var live = await Reload(repo, pr.Id);

            // Eight real saves at once first. SQLite serialises writers, so this rarely interleaves
            // on its own - it is here because it is what the user actually did, not because it is
            // the part with teeth.
            Exception? failure = null;
            var saves = Enumerable.Range(0, 8).Select(async _ =>
            {
                try { await repo.SavePrFieldsAsync(live); }
                catch (Exception ex) { failure ??= ex; }
            });
            await Task.WhenAll(saves);

            Assert(failure == null, $"concurrent saves on a Raw Material PR do not collide; got {failure?.Message}");
            await AssertDerivedDataFreshAsync(db, "after eight concurrent saves");

            // The search index has the same shape of hazard and needed its own teeth: re-indexing a
            // PR is a DELETE then an INSERT of the same rowid, so a second save of the SAME PR that
            // slips between them fails on the insert - "constraint failed", surfaced to the user as
            // a save that did not work. Two approvals signed together did it, because
            // UpdateParentPrApprovalState fires once per approval.
            //
            // Be clear about what this does and does not prove. It checks that a save whose window
            // overlaps an open write transaction completes rather than failing - it does NOT
            // reproduce the collision, and it passed with the fix removed. The interleave that
            // actually fails (delete, delete, insert, insert across two connections) was reproduced
            // by hand against this same SQL; reproducing it from here would mean pausing the
            // repository mid-statement, which there is no hook for. Treat this as a smoke test, and
            // the transaction in RefreshSearchIndexAsync as the thing that carries the guarantee.
            using (var blocker = db.CreateConnection())
            {
                await blocker.OpenAsync();
                var held = (Microsoft.Data.Sqlite.SqliteTransaction)await blocker.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                using (var del = blocker.CreateCommand())
                {
                    del.Transaction = held;
                    del.CommandText = DatabaseConstants.SqlDeleteSearchRow + DatabaseConstants.SqlInsertSearchRow;
                    del.Parameters.AddWithValue("@SearchPrId", live.Id.ToString());
                    await del.ExecuteNonQueryAsync();
                }

                Exception? raced = null;
                var second = Task.Run(async () =>
                {
                    try { await repo.SavePrFieldsAsync(live); }
                    catch (Exception ex) { raced = ex; }
                });

                await Task.Delay(150);              // the second saver is now inside the window
                await held.CommitAsync();
                await second;

                Assert(raced == null,
                    $"re-indexing a PR while another save of it is in flight does not collide; got {raced?.Message}");
            }

            await AssertDerivedDataFreshAsync(db, "after an interleaved re-index");

            // The part with teeth: run the write half twice over a key that is already there, which
            // is exactly what the interleave produces and what used to throw. Written against the
            // shipped SQL, so it fails the moment the upsert is taken back out - checked by removing
            // it and watching this fail.
            var key = Gypsum.Trim().ToLowerInvariant();
            using (var probe = db.CreateConnection())
            {
                await probe.OpenAsync();

                async Task RunHalfAsync(string template)
                {
                    using var cmd = probe.CreateCommand();
                    cmd.CommandText = string.Format(template, "@k0");
                    cmd.Parameters.AddWithValue("@k0", key);
                    await cmd.ExecuteNonQueryAsync();
                }

                Exception? collision = null;
                try
                {
                    await RunHalfAsync(DatabaseConstants.SqlRefreshMaterialAggregatesDeleteTemplate);
                    await RunHalfAsync(DatabaseConstants.SqlRefreshMaterialAggregatesInsertTemplate);
                    // Second writer's insert, with the row already present - the collision.
                    await RunHalfAsync(DatabaseConstants.SqlRefreshMaterialAggregatesInsertTemplate);
                }
                catch (Exception ex)
                {
                    collision = ex;
                }

                Assert(collision == null,
                    $"a second refresh over an existing material row updates it rather than failing; got {collision?.Message}");
            }

            await AssertDerivedDataFreshAsync(db, "after a repeated material refresh");

            // And the figure the Raw & Packing tab reads is still right afterwards.
            using var connection = db.CreateConnection();
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT LineCount, TotalOrdered FROM MaterialAggregate WHERE MaterialKey = @k;";
            cmd.Parameters.AddWithValue("@k", Gypsum.Trim().ToLowerInvariant());
            using var reader = await cmd.ExecuteReaderAsync();
            Assert(await reader.ReadAsync(), "the material still has an aggregate row");
            if (!reader.IsDBNull(0))
            {
                Assert(reader.GetInt32(0) == 1, $"counting one line; got {reader.GetInt32(0)}");
                Assert(reader.GetDecimal(1) == 500m, $"and 500 ordered; got {reader.GetDecimal(1)}");
            }
        }

        /// <summary>The price comparison, which is what actually goes to the approvers - and the one
        /// place a mis-paired line turns into a signed document with the wrong number on it.
        ///
        /// Both exporters used to take the FIRST quote line whose text matched, so on a merged PR
        /// with two identically named lines one vendor's single price was printed against both of
        /// them and the other line's real quote never appeared. This builds exactly that shape - two
        /// PR lines named the same, two vendors, four different prices - and reads the generated
        /// spreadsheet back to check each price landed on its own row.</summary>
        private static async Task PcrExportFlowAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            // First item's name is a spec pasted from one Excel cell - three hard lines. It must
            // reach both exports as three stacked lines, growing the row down, not the column across.
            const string multiLineName = "PUMP CASING GASKET\nSPIRAL WOUND, DN80\nCL300 RF";
            var pr = NewPr("pcr-export", (multiLineName, 12m), (Brush, 33m));
            await repo.SaveAsync(pr);

            // Four distinct prices, so every cell can be told apart from every other.
            var alpha = NewRfq(pr, "alpha");
            alpha.Items.Add(NewRfqLine(pr.Items[0], price: 11.11m));
            alpha.Items.Add(NewRfqLine(pr.Items[1], price: 22.22m));
            await repo.SaveRfqAsync(alpha);

            var beta = NewRfq(pr, "beta");
            beta.Items.Add(NewRfqLine(pr.Items[0], price: 33.33m));
            beta.Items.Add(NewRfqLine(pr.Items[1], price: 44.44m));
            await repo.SaveRfqAsync(beta);

            var pcr = new PriceComparisonRequest { Id = Guid.NewGuid(), PrId = pr.Id, PcrNo = _marker + "-pcr", CreatedAt = DateTime.Now };
            await repo.SavePcrAsync(pcr);
            await AssertDerivedDataFreshAsync(db, "after creating a PCR");

            var read = await Reload(repo, pr.Id);
            var rfqs = read.Rfqs.OrderBy(r => r.Vendor, StringComparer.Ordinal).ToList();
            Assert(rfqs.Count == 2, $"both quotes are on the PR; got {rfqs.Count}");

            var xlsx = Services.Export.PcrExcelExporter.GenerateExcel(read, read.Pcr ?? pcr, rfqs, "flow check");
            Assert(xlsx.Length > 0, "the spreadsheet is produced");

            var sheet = ReadSheetXml(xlsx);
            Assert(!string.IsNullOrEmpty(sheet), "and contains a worksheet");

            // Every one of the four prices has to appear exactly once. The old code printed one
            // vendor's first price twice and dropped the second line's, so a missing value here is
            // precisely the bug coming back.
            foreach (var price in new[] { "11.11", "22.22", "33.33", "44.44" })
            {
                var occurrences = CountOccurrences(sheet, ">" + price + "<");
                Assert(occurrences == 1, $"price {price} appears once in the comparison; got {occurrences}");
            }

            // And the two rows carry their own quantities rather than the first line's twice over.
            Assert(CountOccurrences(sheet, ">12 NOS<") >= 1, "the 12 NOS line is printed with its own quantity");
            Assert(CountOccurrences(sheet, ">33 NOS<") >= 1, "and the 33 NOS line with its own");

            // The multi-line item name keeps its hard breaks in the sheet (Excel wraps on \n only
            // with wrapText + preserved whitespace) and every line's text survives.
            Assert(sheet.Contains("xml:space=\"preserve\""), "the item description cell preserves whitespace");
            Assert(sheet.Contains("PUMP CASING GASKET\nSPIRAL WOUND, DN80\nCL300 RF"),
                "the three-line item name reaches the sheet with its line breaks intact");

            // The PDF shares the pairing but renders to a binary stream, so this is a smoke test:
            // it must build both pages without throwing on the same data.
            var pdf = Services.Export.PcrPdfExporter.GeneratePdf(read, read.Pcr ?? pcr, rfqs, "flow check");
            Assert(pdf.Length > 1000, $"the PDF is produced; got {pdf.Length} bytes");

            // The preview no longer rasterizes every page up front - it opens the document once and
            // draws pages on request, so page one reaches the screen before the rest are done. Every
            // page has to come back as a real PNG from that path, in any order, or the preview shows
            // a blank sheet where a page should be.
            var pageSource = await Services.Export.PcrPdfPageSource.OpenAsync(pdf);
            Assert(pageSource.PageCount >= 1, $"the page source finds the PDF's pages; got {pageSource.PageCount}");
            for (var pageIndex = pageSource.PageCount - 1; pageIndex >= 0; pageIndex--)   // last first: order must not matter
            {
                var png = await pageSource.RenderAsync(pageIndex);
                var isPng = png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47;
                Assert(isPng, $"page {pageIndex + 1} of {pageSource.PageCount} renders on request as a PNG; got {png.Length} bytes");
            }

            // Small paper. The column floors alone used to be wider than an A6 portrait page, so each
            // vendor's columns came out with a NEGATIVE width and overprinted each other. Now the sheet
            // is laid out on a larger virtual page and scaled down to fit.
            Services.Export.PcrPdfOptions Opts(Services.Export.PdfPaperSize size, Services.Export.PdfOrientation o) =>
                new() { PaperSize = size, Orientation = o };
            var a4l = Services.Export.PcrPdfExporter.FitScaleFor(Opts(Services.Export.PdfPaperSize.A4, Services.Export.PdfOrientation.Landscape), 5);
            Assert(a4l == 1.0, $"A4 landscape with five suppliers prints at full size, as it always has; got {a4l:P0}");
            var a4p = Services.Export.PcrPdfExporter.FitScaleFor(Opts(Services.Export.PdfPaperSize.A4, Services.Export.PdfOrientation.Portrait), 5);
            Assert(a4p == 1.0, $"A4 portrait with five suppliers is unchanged too - cramped but never overlapping; got {a4p:P0}");
            var a6p = Services.Export.PcrPdfExporter.FitScaleFor(Opts(Services.Export.PdfPaperSize.A6, Services.Export.PdfOrientation.Portrait), 5);
            Assert(a6p is > 0 and < 0.35, $"A6 portrait with five suppliers is scaled down to fit rather than overprinted; got {a6p:P0}");

            var small = Services.Export.PcrPdfExporter.GeneratePdf(read, read.Pcr ?? pcr, rfqs, "flow check",
                Opts(Services.Export.PdfPaperSize.A6, Services.Export.PdfOrientation.Portrait));
            var smallText = System.Text.Encoding.Latin1.GetString(small);
            Assert(smallText.Contains("/MediaBox [0 0 298 420]"),
                "a scaled A6 sheet still declares real A6 paper - the virtual layout page must never reach the MediaBox, or the printer picks the wrong paper");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(smallText, @"0\.\d{4} 0 0 0\.\d{4} 0 0 cm"),
                "and every page carries the scale that fits it onto that paper");
            var smallSource = await Services.Export.PcrPdfPageSource.OpenAsync(small);
            var smallPng = await smallSource.RenderAsync(0);
            Assert(smallPng.Length > 8 && smallPng[0] == 0x89, "the scaled A6 sheet still renders");

            // More than five suppliers: every vendor is fitted across the page by shrinking the sheet,
            // never split across pages; past six the currency moves into the column headings. Up to
            // five, nothing about the sheet changes.
            var a4Land = Opts(Services.Export.PdfPaperSize.A4, Services.Export.PdfOrientation.Landscape);
            var upToFive = Services.Export.PcrPdfExporter.PrintScaleFor(read, rfqs, a4Land);
            Assert(upToFive == Services.Export.PcrPdfExporter.FitScaleFor(a4Land, rfqs.Count),
                $"with {rfqs.Count} suppliers the print scale is exactly what it always was; got {upToFive:P0}");

            List<RequestForQuotation> Cycle(int count) => Enumerable.Range(0, count).Select(i => rfqs[i % rfqs.Count]).ToList();
            var twelve = Cycle(12);
            var twelveScale = Services.Export.PcrPdfExporter.PrintScaleFor(read, twelve, a4Land);
            Assert(twelveScale is > 0.3 and < 1.0, $"twelve suppliers on A4 landscape are shrunk to fit, not overprinted or clipped; got {twelveScale:P0}");
            var twelveA3 = Services.Export.PcrPdfExporter.PrintScaleFor(read, twelve,
                Opts(Services.Export.PdfPaperSize.A3, Services.Export.PdfOrientation.Landscape));
            Assert(twelveA3 > twelveScale, $"A3 prints twelve suppliers bigger than A4 does; got {twelveA3:P0} vs {twelveScale:P0}");

            var wide = Services.Export.PcrPdfExporter.GeneratePdf(read, read.Pcr ?? pcr, twelve, "fit check", a4Land);
            var wideText = System.Text.Encoding.Latin1.GetString(wide);
            Assert(wideText.Contains("/MediaBox [0 0 842 595]"), "a fitted sheet is still real A4 paper");
            var wideSource = await Services.Export.PcrPdfPageSource.OpenAsync(wide);
            var qtyHeadings = System.Text.RegularExpressions.Regex.Matches(wideText, @"\(Qty\) Tj").Count;
            Assert(qtyHeadings == 12 * wideSource.PageCount,
                $"every page carries all twelve vendors' columns; got {qtyHeadings} Qty headings over {wideSource.PageCount} page(s)");
            Assert(wideText.Contains("(Price \\("),
                "past six suppliers each vendor's price heading carries its currency");
            var widePng = await wideSource.RenderAsync(0);
            Assert(widePng.Length > 8 && widePng[0] == 0x89, "the fitted twelve-supplier sheet renders");

            // Prints go out black and white at 600 DPI - never past the printer's own resolution - and
            // neither A4 nor A3 is held down by the render size cap.
            var wideDpi = Services.Export.PcrPdfRasterizer.PrintDpiFor(0);
            Assert(wideDpi == 600, $"a print is rendered at 600 DPI; got {wideDpi}");
            var cappedDpi = Services.Export.PcrPdfRasterizer.PrintDpiFor(300);
            Assert(cappedDpi == 300, $"but never past a 300 DPI printer's own resolution; got {cappedDpi}");
            using (var printSource = await Services.Export.PcrPdfPageSource.OpenAsync(wide, wideDpi))
            {
                Assert(Math.Abs(printSource.EffectiveDpi - 600) < 0.01, $"A4 prints at the full 600 DPI; got {printSource.EffectiveDpi:0}");
                var mono = await printSource.RenderMonochromeAsync(0);
                // 842 x 595 pt at 600 DPI.
                Assert(mono.Width == 7017 && mono.Height == 4958 && mono.Bits.Length == mono.Stride * mono.Height,
                    $"an A4 landscape page renders black and white at 7017x4958; got {mono.Width}x{mono.Height}");
                long black = 0;
                foreach (var b in mono.Bits) black += 8 - System.Numerics.BitOperations.PopCount(b);
                var inkShare = black / (double)(mono.Width * (long)mono.Height);
                Assert(inkShare is > 0.005 and < 0.5, $"and carries the sheet - some ink, mostly paper; got {inkShare:P1} black");

                // Two corners saved beside the check's logs, so a person can look at exactly what a printer
                // receives: the top-left (title, headings, first rows) and the right-hand vendor columns.
                foreach (var (name, left, top) in new[] { ("pcr-print-left.png", 0, 0), ("pcr-print-right.png", mono.Width - 2400, 0) })
                {
                    const int cw = 2400, ch = 1400;
                    var bgra = new byte[cw * ch * 4];
                    for (int y = 0; y < ch; y++)
                        for (int x = 0; x < cw; x++)
                        {
                            var sx = left + x; var sy = top + y;
                            var white = (mono.Bits[sy * mono.Stride + (sx >> 3)] & (0x80 >> (sx & 7))) != 0;
                            var o = (y * cw + x) * 4;
                            bgra[o] = bgra[o + 1] = bgra[o + 2] = white ? (byte)255 : (byte)0; bgra[o + 3] = 255;
                        }
                    using var dumpStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, dumpStream);
                    encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                        cw, ch, mono.Dpi, mono.Dpi, bgra);
                    await encoder.FlushAsync();
                    var png = new byte[dumpStream.Size];
                    using var reader = new Windows.Storage.Streams.DataReader(dumpStream.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)dumpStream.Size);
                    reader.ReadBytes(png);
                    await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(DatabaseConstants.DatabaseDirectory, name), png);
                }
            }
            var a3Wide = Services.Export.PcrPdfExporter.GeneratePdf(read, read.Pcr ?? pcr, twelve, "fit check",
                Opts(Services.Export.PdfPaperSize.A3, Services.Export.PdfOrientation.Landscape));
            using (var a3Source = await Services.Export.PcrPdfPageSource.OpenAsync(a3Wide, wideDpi))
                Assert(Math.Abs(a3Source.EffectiveDpi - 600) < 0.01, $"A3 prints at the full 600 DPI too; got {a3Source.EffectiveDpi:0}");

            // The fit-to-one-page shrink is part of how small the text prints.
            var onePage = Services.Export.PcrPdfExporter.GeneratePdfDocument(read, read.Pcr ?? pcr, twelve, "fit check",
                a4Land with { LayoutMode = Services.Export.PdfLayoutMode.ShrinkToFit });
            var fitOnly = Services.Export.PcrPdfExporter.GeneratePdfDocument(read, read.Pcr ?? pcr, twelve, "fit check", a4Land);
            Assert(Math.Abs(fitOnly.TextScale - twelveScale) < 0.0001, $"without fit-to-one-page the reported scale is the width fit; got {fitOnly.TextScale:P1} vs {twelveScale:P1}");
            Assert(onePage.TextScale <= fitOnly.TextScale, $"with it the reported scale includes that extra shrink; got {onePage.TextScale:P1} vs {fitOnly.TextScale:P1}");

            // A long run of RFQ numbers wraps onto more lines at full size instead of being squeezed
            // onto one and running off the page; a one-word vendor name is broken across the heading
            // instead of being cut off with "..".
            var savedNos = rfqs.Select(r => r.RfqNo).ToList();
            var savedVendor = rfqs[0].Vendor;
            try
            {
                // No long shared prefix, so the sheet cannot shorten them into one compact run.
                for (int i = 0; i < rfqs.Count; i++) rfqs[i].RfqNo = $"RFQ-{(char)('K' + i)}{(i + 3) * 7919 % 99991:D5}-PROCUREMENT";
                rfqs[0].Vendor = "InternationalIndustrialTradingCompanyLLC";
                var fifteen = Cycle(15);
                var longText = System.Text.Encoding.Latin1.GetString(
                    Services.Export.PcrPdfExporter.GeneratePdf(read, read.Pcr ?? pcr, fifteen, "wrap check", a4Land));
                var rfqLines = System.Text.RegularExpressions.Regex.Matches(longText, @"\([^()]*PROCUREMENT[^()]*\) Tj").Count;
                Assert(rfqLines >= 2, $"fifteen long RFQ numbers wrap onto more than one line; got {rfqLines} line(s)");
                var rfqFont = System.Text.RegularExpressions.Regex.Match(longText, @"/F2 (\d+\.\d) Tf\s+[\d. ]+rg\s+[\d. ]+Td\s+\(RFQ Number");
                Assert(rfqFont.Success && rfqFont.Groups[1].Value == "8.5", $"at the normal 8.5 pt, not shrunk; got {(rfqFont.Success ? rfqFont.Groups[1].Value : "no match")}");
                Assert(!System.Text.RegularExpressions.Regex.IsMatch(longText, @"\(International[^()]*\.\.\) Tj"),
                    "a one-word vendor name is not cut off with an ellipsis");
                Assert(longText.Contains("(International"), "and its first part is in the heading");
            }
            finally
            {
                for (int i = 0; i < rfqs.Count; i++) rfqs[i].RfqNo = savedNos[i];
                rfqs[0].Vendor = savedVendor;
            }

            // The preview's sharp zoom draws just a region of the page, at exactly the size asked for.
            var (pw, ph) = wideSource.PageSizeDips(0);
            using (var region = await wideSource.RenderRegionAsync(0, new Windows.Foundation.Rect(pw / 4, ph / 4, pw / 4, ph / 4), 640, 400))
            {
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(region);
                Assert(decoder.PixelWidth == 640 && decoder.PixelHeight == 400,
                    $"a zoomed region renders at the requested size; got {decoder.PixelWidth}x{decoder.PixelHeight}");
            }

            var sixText = System.Text.Encoding.Latin1.GetString(
                Services.Export.PcrPdfExporter.GeneratePdf(read, read.Pcr ?? pcr, Cycle(6), "fit check", a4Land));
            Assert(!sixText.Contains("(Price \\(") && sixText.Contains("(Unit Price) Tj"),
                "six suppliers still print the currency in the cells, with plain Unit Price headings");

            // And the all-pages path printing uses still agrees with it on the count.
            var (allPages, _) = await Services.Export.PcrPdfRasterizer.RenderPagesAsync(pdf);
            Assert(allPages.Count == pageSource.PageCount,
                $"printing's full render and the preview's page source agree on the page count; got {allPages.Count} vs {pageSource.PageCount}");

            // Each hard line of the item name is drawn as its own PDF text op - never one mangled
            // string with a raw newline in it.
            var pdfText = System.Text.Encoding.Latin1.GetString(pdf);
            Assert(pdfText.Contains("(PUMP CASING GASKET) Tj"), "the PDF draws the item name's first line on its own");
            Assert(pdfText.Contains("(SPIRAL WOUND, DN80) Tj"), "and its second line");
            Assert(pdfText.Contains("(CL300 RF) Tj"), "and its third line");
        }

        private static string ReadSheetXml(byte[] xlsx)
        {
            using var ms = new MemoryStream(xlsx);
            using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml");
            if (entry == null) return string.Empty;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        // ---- fixtures -------------------------------------------------------------------------

        private static PurchaseRequisition NewPr(string tag, params (string Name, decimal Qty)[] items)
        {
            var pr = new PurchaseRequisition
            {
                Id = Guid.NewGuid(),
                PrNo = $"{_marker}-{tag}",
                Description = $"{_marker} {tag}",
                Requestor = "flow check",
                Plant = ProcurementPlant.RW01,
                PrType = ProcurementPrType.StoresAndSpares
            };
            int sort = 0;
            foreach (var (name, qty) in items)
            {
                pr.Items.Add(new PrItem { Id = Guid.NewGuid(), PrId = pr.Id, ItemName = name, Quantity = qty, Unit = "NOS", SortOrder = sort++ });
            }
            Created.Add(pr.Id);
            return pr;
        }

        private static RequestForQuotation NewRfq(PurchaseRequisition pr, string vendor) => new()
        {
            Id = Guid.NewGuid(),
            PrId = pr.Id,
            RfqNo = $"{_marker}-rfq-{vendor}",
            Vendor = $"{_marker}-{vendor}",
            Currency = "AED",
            VatType = "5%",
            Status = RfqStatus.QuoteReceived,
            QuoteReceivedDate = DateTime.Today
        };

        private static RfqItem NewRfqLine(PrItem prItem, decimal price) => new()
        {
            Id = Guid.NewGuid(),
            PrItemId = prItem.Id,
            ItemName = prItem.ItemName,
            Quantity = prItem.Quantity,
            Unit = prItem.Unit,
            IsQuoted = true,
            QuotedUnitPrice = price
        };

        private static PurchaseOrder NewPo(PurchaseRequisition pr, RequestForQuotation rfq, string tag) => new()
        {
            Id = Guid.NewGuid(),
            PrId = pr.Id,
            LinkedRfqId = rfq.Id,
            PoNo = $"{_marker}-{tag}",
            Vendor = rfq.Vendor,
            Currency = "AED",
            VatType = "5%",
            Status = PoStatus.Raised,
            Date = DateTime.Today
        };

        private static PurchaseOrderItem NewPoLine(PrItem prItem, decimal qty, decimal price) => new()
        {
            Id = Guid.NewGuid(),
            PrItemId = prItem.Id,
            ItemName = prItem.ItemName,
            Quantity = qty,
            Unit = prItem.Unit,
            UnitPrice = price
        };

        private static async Task<PurchaseRequisition> Reload(IPurchaseRequisitionRepository repo, Guid id)
        {
            var rows = await repo.GetByIdsAsync(new[] { id });
            if (rows.Count == 0) throw new InvalidOperationException($"PR {id} vanished mid-check.");
            return rows[0];
        }

        // ---- assertions and measurement -------------------------------------------------------

        private static void Assert(bool condition, string what)
        {
            _assertions++;
            if (!condition) Failures.Add(what);
        }

        /// <summary>Both denormalised tables, re-checked after every write. This is the assertion that
        /// catches a new write path forgetting to maintain them - the failure mode is otherwise
        /// completely silent: search stops finding things, Raw &amp; Packing shows stale balances.</summary>
        private static async Task AssertDerivedDataFreshAsync(SqliteDatabase db, string step)
        {
            using var connection = db.CreateConnection();
            await connection.OpenAsync();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = DatabaseConstants.SqlStaleSearchRowCount;
                var stale = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert(stale == 0, $"search text is current {step} ({stale} stale row(s))");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = DatabaseConstants.SqlStaleMaterialAggregateCount;
                var stale = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert(stale == 0, $"material aggregates are current {step} ({stale} disagreeing row(s))");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = DatabaseConstants.SqlStaleVendorAggregateCount;
                var stale = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert(stale == 0, $"vendor suggestions are current {step} ({stale} disagreeing row(s))");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = DatabaseConstants.SqlStalePoDateCount;
                var stale = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert(stale == 0, $"PO lines carry their PO's date {step} ({stale} disagreeing line(s))");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = DatabaseConstants.SqlStaleItemPriceCount;
                var stale = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert(stale == 0, $"quote dates and the items list are current {step} ({stale} disagreeing row(s))");
            }

            // Orphans: a line pointing at a PR line that no longer exists is how the original bug
            // hid. Nothing should create one.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT (SELECT COUNT(*) FROM RfqItem WHERE PrItemId IS NOT NULL AND PrItemId NOT IN (SELECT Id FROM PrItem))
     + (SELECT COUNT(*) FROM PurchaseOrderItem WHERE PrItemId IS NOT NULL AND PrItemId NOT IN (SELECT Id FROM PrItem));";
                var dangling = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert(dangling == 0, $"no line points at a deleted PR line {step} ({dangling} dangling)");
            }
        }

        private static async Task Measure(string name, Func<Task> phase)
        {
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            // A gen2 that runs in the background costs the UI nothing; a full BLOCKING gen2 stops
            // every thread, and at these heap sizes that is a visible hitch. This API returns the
            // GLOBAL index of the most recent full-blocking collection, not a count of them - so the
            // only sound reading is "did the index move", i.e. did at least one happen in this phase.
            var blockingIndexBefore = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
            var heapBefore = GC.GetTotalMemory(false);
            var checksBefore = _assertions;

            var sw = Stopwatch.StartNew();
            await phase();
            sw.Stop();

            var heapAfter = GC.GetTotalMemory(false);
            var info = GC.GetGCMemoryInfo();
            // GenerationInfo is gen0, gen1, gen2, large object heap, pinned object heap. An object of
            // 85,000 bytes or more lands on the LOH, which is only ever collected with a gen2 - so a
            // growing LOH is the usual reason every collection turns into a full one.
            var lohBytes = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
            using var proc = Process.GetCurrentProcess();

            Phases.Add(new Phase(
                name,
                sw.Elapsed.TotalMilliseconds,
                (heapAfter - heapBefore) / 1024,
                heapAfter / 1024,
                proc.WorkingSet64 / (1024 * 1024),
                GC.CollectionCount(0) - gen0,
                GC.CollectionCount(1) - gen1,
                GC.CollectionCount(2) - gen2,
                GC.GetGCMemoryInfo(GCKind.FullBlocking).Index != blockingIndexBefore ? 1 : 0,
                lohBytes / 1024,
                info.PauseTimePercentage,
                _assertions - checksBefore));
        }

        private static void Report(double totalSeconds, bool gcCountsContaminated)
        {
            var passed = Failures.Count == 0;
            var summary = passed
                ? $"PASS  {_assertions} checks across {Phases.Count} phases in {totalSeconds:F1}s"
                : $"FAIL  {Failures.Count} of {_assertions} checks across {Phases.Count} phases in {totalSeconds:F1}s";

            Debug.WriteLine("ProcurementFlowSelfCheck: " + summary);
            CrashLog.Write("PROCUREMENT FLOW SELF-CHECK " + summary);

            var log = new StringBuilder();
            log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {summary}");
            log.AppendLine();
            log.AppendLine("phase                                          ms  checks  heap KB  heap d KB  WS MB  gen0 gen1 gen2  blocked?  LOH KB  pause%");
            foreach (var p in Phases)
            {
                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-44} {1,6:F0} {2,7} {3,8} {4,10} {5,6} {6,5} {7,4} {8,4} {9,9} {10,7} {11,7:F2}",
                    p.Name, p.Ms, p.Checks, p.HeapKb, p.HeapDeltaKb, p.WorkingSetMb,
                    p.Gen0, p.Gen1, p.Gen2, p.SawBlockingGen2, p.LohKb, p.PausePct));
            }
            log.AppendLine();
            log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "totals: gen0 {0}, gen1 {1}, gen2 {2}; phases that saw a full blocking gen2: {3} of {4}; time paused for GC: {5:F2}%",
                Phases.Sum(x => x.Gen0), Phases.Sum(x => x.Gen1), Phases.Sum(x => x.Gen2),
                Phases.Sum(x => x.SawBlockingGen2), Phases.Count,
                Phases.Count == 0 ? 0 : Phases[^1].PausePct));
            if (gcCountsContaminated)
            {
                log.AppendLine();
                log.AppendLine("NOTE: another self-check that forces collections ran alongside this one.");
                log.AppendLine("      The gen0/gen1/gen2 columns above include those forced collections and");
                log.AppendLine("      overstate what this app does on its own. Re-run with PROCURE_FLOW_SELFCHECK");
                log.AppendLine("      alone for GC figures you can quote.");
            }
            if (!passed)
            {
                log.AppendLine();
                log.AppendLine("FAILURES");
                foreach (var f in Failures) log.AppendLine("  - " + f);
            }

            var csv = new StringBuilder();
            csv.AppendLine("phase,ms,checks,heap_kb,heap_delta_kb,working_set_mb,gen0,gen1,gen2,saw_blocking_gen2,loh_kb,pause_pct");
            foreach (var p in Phases)
            {
                csv.AppendLine(string.Format(CultureInfo.InvariantCulture, "\"{0}\",{1:F1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11:F2}",
                    p.Name, p.Ms, p.Checks, p.HeapKb, p.HeapDeltaKb, p.WorkingSetMb,
                    p.Gen0, p.Gen1, p.Gen2, p.SawBlockingGen2, p.LohKb, p.PausePct));
            }

            try
            {
                File.WriteAllText(Path.Combine(DatabaseConstants.DatabaseDirectory, "procurement-flow-selfcheck.log"), log.ToString());
                File.WriteAllText(Path.Combine(DatabaseConstants.DatabaseDirectory, "procurement-flow-metrics.csv"), csv.ToString());
            }
            catch
            {
                // A diagnostic must never be the thing that breaks the run.
            }
        }
    }
}
