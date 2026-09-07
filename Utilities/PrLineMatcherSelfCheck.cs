using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Procure.Models;

namespace Procure.Utilities
{
    /// <summary>
    /// The runnable check behind <see cref="PrLineMatcher"/>. It rebuilds the exact situation that
    /// produced the bug - a consolidated PR asking for 12 NOS and 33 NOS of one identically named
    /// item, quoted on two lines where only one carries a saved link - and asserts the two lines
    /// land on two different PR lines.
    ///
    /// That case is worth a permanent check because it fails silently: the old flat
    /// "same id OR same name" match produced perfectly plausible numbers (12 and 12), and the only
    /// symptom was a PO the wizard refused to raise.
    ///
    /// Run it by launching a Debug build with PROCURE_SELFCHECK=1 set. No database is touched.
    /// </summary>
    internal static class PrLineMatcherSelfCheck
    {
        private const string Brush = "CARBON BRUSH,25X50X50MM F/FRAME:M15E";

        public static void Run()
        {
            try
            {
                DuplicateNamesGetSeparateLines();
                SavedLinkBeatsNameCollision();
                ExtraLineStillCounts();
                OrderedQuantityIsNotDoubleCounted();
                WizardBannerAgreesWithTheRows();
                SplitLinesOnOnePrItemSumAndCap();
                CoverageCountsAgainstTheRequisition();
                CutBackRequisitionReadsOverOrdered();
                MapReusesCallerBuffers();
                BadgeTextGetsTheRightColour();
                Debug.WriteLine("PR LINE MATCHER SELF-CHECKS PASSED");
                CrashLog.Write("PR LINE MATCHER SELF-CHECKS PASSED");
            }
            catch (Exception ex)
            {
                // Debug.WriteLine alone is invisible unless a debugger is attached, and this runs at
                // startup where an unhandled throw surfaces as an unrelated XamlRoot error.
                Debug.WriteLine("PR LINE MATCHER SELF-CHECKS FAILED: " + ex);
                CrashLog.Write("PR LINE MATCHER SELF-CHECKS FAILED", ex);
                throw;
            }
        }

        /// <summary>The reported bug: both quote lines used to resolve to the 12 NOS line.</summary>
        private static void DuplicateNamesGetSeparateLines()
        {
            var twelve = PrLine(Brush, 12m, 0);
            var thirtyThree = PrLine(Brush, 33m, 1);

            // Neither quote line carries a link - exactly what the merge used to leave behind.
            var a = new RfqItem { Id = Guid.NewGuid(), ItemName = Brush, Quantity = 12m, SortOrder = 0 };
            var b = new RfqItem { Id = Guid.NewGuid(), ItemName = Brush, Quantity = 33m, SortOrder = 1 };

            var map = PrLineMatcher.Map(new[] { a, b }, new[] { twelve, thirtyThree });

            Assert(map[a].Id == twelve.Id, "first unlinked quote line takes the first PR line");
            Assert(map[b].Id == thirtyThree.Id, "second unlinked quote line takes the SECOND PR line, not the first again");
            Assert(map[b].Quantity == 33m, $"the 33 NOS target survives; got {map[b].Quantity}");
        }

        /// <summary>A saved link must not lose to a same-named sibling that comes first.</summary>
        private static void SavedLinkBeatsNameCollision()
        {
            var twelve = PrLine(Brush, 12m, 0);
            var thirtyThree = PrLine(Brush, 33m, 1);

            // Linked to the SECOND line while sitting first in the list.
            var linked = new RfqItem { Id = Guid.NewGuid(), PrItemId = thirtyThree.Id, ItemName = Brush, Quantity = 33m, SortOrder = 0 };
            var loose = new RfqItem { Id = Guid.NewGuid(), ItemName = Brush, Quantity = 12m, SortOrder = 1 };

            var map = PrLineMatcher.Map(new[] { linked, loose }, new[] { twelve, thirtyThree });

            Assert(map[linked].Id == thirtyThree.Id, "the saved link wins over list order");
            Assert(map[loose].Id == twelve.Id, "the unlinked line takes the line the link did not claim");
        }

        /// <summary>More quote lines than PR lines is over-allocation, not something to hide.</summary>
        private static void ExtraLineStillCounts()
        {
            var only = PrLine(Brush, 12m, 0);
            var a = new RfqItem { Id = Guid.NewGuid(), ItemName = Brush, Quantity = 12m, SortOrder = 0 };
            var b = new RfqItem { Id = Guid.NewGuid(), ItemName = Brush, Quantity = 5m, SortOrder = 1 };

            var map = PrLineMatcher.Map(new[] { a, b }, new[] { only });

            Assert(map.Count == 2, $"the surplus line still resolves so the allocation check sees it; got {map.Count}");
            Assert(map[b].Id == only.Id, "it falls back to the only line with that name");
        }

        /// <summary>One PO for the 12 must not also credit the 33 line.</summary>
        private static void OrderedQuantityIsNotDoubleCounted()
        {
            var twelve = PrLine(Brush, 12m, 0);
            var thirtyThree = PrLine(Brush, 33m, 1);

            var po = new PurchaseOrder { Id = Guid.NewGuid(), PoNo = "PO-SELFCHECK" };
            po.Items.Add(new PurchaseOrderItem { Id = Guid.NewGuid(), PrItemId = twelve.Id, ItemName = Brush, Quantity = 12m, SortOrder = 0 });

            var ordered = PrLineMatcher.OrderedQuantities(new[] { twelve, thirtyThree }, new[] { po });

            Assert(ordered[twelve.Id] == 12m, $"the ordered line is credited once; got {ordered[twelve.Id]}");
            Assert(ordered[thirtyThree.Id] == 0m, $"its same-named sibling is credited nothing; got {ordered[thirtyThree.Id]}");

            // And a second PO against the same line adds to it rather than replacing it.
            var po2 = new PurchaseOrder { Id = Guid.NewGuid(), PoNo = "PO-SELFCHECK-2" };
            po2.Items.Add(new PurchaseOrderItem { Id = Guid.NewGuid(), PrItemId = thirtyThree.Id, ItemName = Brush, Quantity = 20m, SortOrder = 0 });
            var both = PrLineMatcher.OrderedQuantities(new[] { twelve, thirtyThree }, new[] { po, po2 });
            Assert(both[thirtyThree.Id] == 20m, $"a separate PO's quantity lands on its own line; got {both[thirtyThree.Id]}");
        }

        /// <summary>The whole reported scenario end to end: PR 12088057/12083538, 12 NOS + 33 NOS of
        /// one identically named brush, one vendor quoting both on a card where the second line was
        /// added by hand and so carries no link. The banner's per-line totals and the rows' own
        /// badges have to tell the same story - they used to say 24-against-12 and "Fully Allocated"
        /// at the same time.</summary>
        private static void WizardBannerAgreesWithTheRows()
        {
            var twelve = PrLine(Brush, 12m, 0);
            var thirtyThree = PrLine(Brush, 33m, 1);
            var prLines = new[] { twelve, thirtyThree };

            var rowA = new PoRfqItemSelection { PrItemId = twelve.Id, ItemName = Brush, Quantity = 12m, Unit = "NOS", IsSelected = true };
            var rowB = new PoRfqItemSelection { ItemName = Brush, Quantity = 33m, Unit = "NOS", IsSelected = true };
            var card = new[] { rowA, rowB };

            var inWindow = PrLineMatcher.PendingQuantities(prLines, new[] { (IEnumerable<PoRfqItemSelection>?)card });
            Assert(inWindow[twelve.Id] == 12m, $"the 12 NOS line is allocated 12; got {inWindow[twelve.Id]}");
            Assert(inWindow[thirtyThree.Id] == 33m, $"the 33 NOS line is allocated 33, not 12 again; got {inWindow[thirtyThree.Id]}");

            // Neither line is over target, so the banner stays clear.
            foreach (var line in prLines)
            {
                Assert(inWindow[line.Id] <= line.Quantity, $"'{line.ItemName}' is not over-allocated at {line.Quantity} NOS");
            }

            // And each row's own badge, fed the same numbers the banner used, agrees.
            var map = PrLineMatcher.Map(card, prLines);
            foreach (var row in card)
            {
                var line = map[row];
                row.PrTargetQuantity = line.Quantity;
                row.OtherPosOrderedQuantity = 0m;
                row.OtherRowsQuantity = inWindow[line.Id] - row.Quantity;
                Assert(row.IsFullyAllocated, $"row for {line.Quantity} NOS reads fully allocated");
                Assert(!row.IsOverAllocated, $"row for {line.Quantity} NOS does not also claim to be over-allocated");
            }
        }

        /// <summary>The "Add line" button in the PO wizard makes a second row for a PR item that is
        /// already shown, both rows carrying the same saved PrItemId. They must sum against that one
        /// PR line - not each get their own target - so the over-allocation banner still fires.</summary>
        private static void SplitLinesOnOnePrItemSumAndCap()
        {
            var line = PrLine(Brush, 20m, 0);
            var prLines = new[] { line };

            var rowA = new PoRfqItemSelection { PrItemId = line.Id, ItemName = Brush, Quantity = 8m, IsSelected = true };
            var rowB = new PoRfqItemSelection { PrItemId = line.Id, ItemName = Brush, Quantity = 7m, IsSelected = true };
            var card = new[] { rowA, rowB };

            var within = PrLineMatcher.PendingQuantities(prLines, new[] { (IEnumerable<PoRfqItemSelection>?)card });
            Assert(within[line.Id] == 15m, $"both linked rows land on the one PR line; got {within[line.Id]}");
            Assert(within[line.Id] <= line.Quantity, "15 of 20 is within target, banner stays clear");

            rowB.Quantity = 15m; // 8 + 15 = 23 > 20
            var over = PrLineMatcher.PendingQuantities(prLines, new[] { (IEnumerable<PoRfqItemSelection>?)card });
            Assert(over[line.Id] == 23m, $"the split total is summed, not collapsed; got {over[line.Id]}");
            Assert(over[line.Id] > line.Quantity, "the wizard sees the over-allocation and can block the save");
        }

        /// <summary>A quote that lists one of the PR's two lines is Partial, not Full - the chip used
        /// to count the quote against itself and read "Full Quote (1 items)".</summary>
        private static void CoverageCountsAgainstTheRequisition()
        {
            var pr = new PurchaseRequisition { PrNo = "plmsc" };
            var brush = PrLine(Brush, 12m, 0);
            var gasket = PrLine("GASKET,SPIRAL WOUND,DN50", 4m, 1);
            pr.Items.Add(brush);
            pr.Items.Add(gasket);

            var rfq = new RequestForQuotation { Id = Guid.NewGuid(), PrId = pr.Id, Vendor = "Self Check Supplies", Status = RfqStatus.QuoteReceived };
            rfq.Items.Add(new RfqItem { Id = Guid.NewGuid(), PrItemId = brush.Id, ItemName = Brush, Quantity = 12m, IsQuoted = true, QuotedUnitPrice = 15.84m });
            pr.Rfqs.Add(rfq);

            pr.NotifyHierarchyChanged();

            Assert(rfq.PrLineCount == 2, $"the quote knows the PR has 2 lines; got {rfq.PrLineCount}");
            Assert(rfq.PrLinesCovered == 1, $"it lists 1 of them; got {rfq.PrLinesCovered}");
            Assert(rfq.PrLinesMissing == 1, "so one line is missing from it");
            Assert(!rfq.IsFullQuote, "a quote covering half the requisition is not a full quote");
            Assert(rfq.QuoteCompletenessBadge == "Partial (1 of 2)", $"badge reads against the PR; got '{rfq.QuoteCompletenessBadge}'");

            // Quoted for 12, requisition later says 20: the quote keeps 12 and raises the flag.
            brush.Quantity = 20m;
            pr.NotifyHierarchyChanged();
            Assert(rfq.HasQuantityDrift, "a priced line whose PR quantity moved is flagged for re-pricing");
        }

        /// <summary>40 ordered, requisition cut to 25: the line must not read "Complete".</summary>
        private static void CutBackRequisitionReadsOverOrdered()
        {
            var pr = new PurchaseRequisition { PrNo = "plmsc-over" };
            var line = PrLine(Brush, 25m, 0);
            pr.Items.Add(line);

            var po = new PurchaseOrder { Id = Guid.NewGuid(), PrId = pr.Id, PoNo = "PO-SELFCHECK-3" };
            po.Items.Add(new PurchaseOrderItem { Id = Guid.NewGuid(), PrItemId = line.Id, ItemName = Brush, Quantity = 40m });
            pr.Pos.Add(po);

            pr.NotifyHierarchyChanged();

            Assert(line.IsOverOrdered, "the line knows it is over-ordered");
            Assert(line.OverOrderedQuantity == 15m, $"by 15 NOS; got {line.OverOrderedQuantity}");
            Assert(pr.HasOverOrderedItems, "and so does the PR");
            Assert(pr.PoFulfillmentBadgeText.Contains("Over-ordered", StringComparison.Ordinal),
                $"the PR badge says so instead of Complete; got '{pr.PoFulfillmentBadgeText}'");
        }

        /// <summary>The buffer-reusing overload has to produce exactly what the allocating one does -
        /// it is the version the board's reload path runs, once per quote per refresh.</summary>
        private static void MapReusesCallerBuffers()
        {
            var twelve = PrLine(Brush, 12m, 0);
            var thirtyThree = PrLine(Brush, 33m, 1);
            var targets = new List<PrItem> { twelve, thirtyThree };

            var a = new RfqItem { Id = Guid.NewGuid(), ItemName = Brush, Quantity = 12m, SortOrder = 0 };
            var b = new RfqItem { Id = Guid.NewGuid(), ItemName = Brush, Quantity = 33m, SortOrder = 1 };

            var buffer = new Dictionary<RfqItem, PrItem>();
            var claimed = new bool[targets.Count];

            PrLineMatcher.MapInto(new[] { a, b }, targets, buffer, claimed);
            Assert(buffer[a].Id == twelve.Id && buffer[b].Id == thirtyThree.Id, "first pass pairs one-to-one");

            // Second pass over a single line must not inherit the first pass's claims.
            PrLineMatcher.MapInto(new[] { b }, targets, buffer, claimed);
            Assert(buffer.Count == 1, $"the buffer is cleared between passes; got {buffer.Count} entries");
            Assert(buffer[b].Id == twelve.Id, "and the claim marks are reset, so it takes the first free line");
        }

        /// <summary>Every badge on the board picks its colour by looking for words in its own text.
        /// That is fragile by construction: add a new state, forget the word, and it silently renders
        /// in the neutral grey that means "nothing to see here" - which on an over-ordered line is
        /// the opposite of the truth. These are the states added for the requisition/quote sync.</summary>
        private static void BadgeTextGetsTheRightColour()
        {
            void Expect(string text, int state, string what)
                => Assert(PoFulfillmentPalette.Classify(text) == state,
                    $"{what} - '{text}' classified as {PoFulfillmentPalette.Classify(text)}, expected {state}");

            // Red: more has been committed than the requisition asks for.
            Expect("Over-ordered by 15 NOS (Ordered: 40, PR asks for 25)", PoFulfillmentPalette.Over, "an over-ordered line is red");
            Expect("PO: Over-ordered (1 of 2 items - 15 more than the PR asks for)", PoFulfillmentPalette.Over, "so is the PR badge");
            Expect("Exceeds PR target by 12 NOS", PoFulfillmentPalette.Over, "and an over-allocated wizard row");

            // Amber: not settled yet.
            Expect("Missing 1 item", PoFulfillmentPalette.Pending, "a quote missing a PR line is amber");
            Expect("Qty changed since quote (1 item)", PoFulfillmentPalette.Pending, "so is a drifted quantity");
            Expect("Not on the requisition - extra line (33 NOS)", PoFulfillmentPalette.Pending, "and an unbudgeted row");
            Expect("PR Target: 12 NOS - 5 NOS Pending", PoFulfillmentPalette.Pending, "and a pending quantity");

            // Green: done.
            Expect("PR Target: 12 NOS (This PO: 12) - Fully Allocated", PoFulfillmentPalette.Complete, "a fully allocated row is green");
            Expect("Ordered: 12/12 NOS (Complete)", PoFulfillmentPalette.Complete, "so is a completed line");

            // And "Over-ordered" must not be read as "ordered/complete" just because it contains it.
            Assert(PoFulfillmentPalette.Classify("Over-ordered by 15 NOS") != PoFulfillmentPalette.Complete,
                "an over-ordered line is never shown as complete");
        }

        private static PrItem PrLine(string name, decimal qty, int sort) =>
            new PrItem { Id = Guid.NewGuid(), ItemName = name, Quantity = qty, Unit = "NOS", SortOrder = sort };

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("PrLineMatcher: " + what);
        }
    }
}
