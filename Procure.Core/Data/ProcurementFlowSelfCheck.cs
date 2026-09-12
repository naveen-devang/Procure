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
                await Measure("10 guard decisions (ordered / quoted)", () => GuardDecisionsAsync(db, repo));
                await Measure("11 deletes and cascade", () => DeleteFlowAsync(db, repo));
                await Measure("12 PCR export (Excel + PDF)", () => PcrExportFlowAsync(db, repo));
                await Measure("13 concurrent saves on one material", () => ConcurrentMaterialSavesAsync(db, repo));
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
            Assert(openAfter.BaseAmount == 12m * 15m, $"the quote total is unchanged; got {openAfter.BaseAmount}");

            var renamedInOpen = openAfter.Items.Single(i => i.PrItemId == pr.Items[0].Id);
            Assert(renamedInOpen.ItemName.EndsWith("(RENAMED)", StringComparison.Ordinal),
                $"the rename reached the open quote; got '{renamedInOpen.ItemName}'");
            Assert(renamedInOpen.Quantity == 12m,
                $"but a priced line keeps the vendor's own quantity; got {renamedInOpen.Quantity}");

            Assert(orderedAfter.Items.Count == 1, "the ordered quote gained nothing - it is a paper trail now");
            Assert(!orderedAfter.Items[0].ItemName.EndsWith("(RENAMED)", StringComparison.Ordinal),
                "and kept its wording, because a raised order must not rewrite itself");

            after.NotifyHierarchyChanged();
            Assert(orderedAfter.HasQuantityDrift, "the ordered quote is flagged as needing a re-ask");
            Assert(!string.IsNullOrEmpty(summary), $"and the user is told what moved; got '{summary}'");
            Assert(summary.Contains("added", StringComparison.OrdinalIgnoreCase), $"summary names the addition; got '{summary}'");
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
