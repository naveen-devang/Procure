using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;

namespace Procure.Data
{
    /// <summary>
    /// Covers the Raw &amp; Packing tab's two bounded queries, which replaced one full load of every
    /// eligible PO item into memory.
    ///
    /// The full load made grouping trivially correct: one list, grouped in LINQ. Splitting it into a
    /// SQL aggregate plus a per-material fetch moves that correctness into two queries that have to
    /// agree with each other - same trim, same case-insensitive match, same search filter. A summary
    /// whose totals disagree with its own lines is the failure mode that reaches the screen as a
    /// wrong balance percentage, so that agreement is what this asserts.
    ///
    /// Runs with PROCURE_SELFCHECK=1. Creates one Raw Material PR with two POs and removes it.
    /// </summary>
    internal static class CallOffSelfCheck
    {
        public static async Task RunAsync(ICallOffRepository callOffs, IPurchaseRequisitionRepository prRepo)
        {
            var marker = "cosc-" + Guid.NewGuid().ToString("N")[..8];
            var materialA = marker + "-steel";
            var materialB = marker + "-resin";

            var pr = new PurchaseRequisition
            {
                PrNo = marker,
                Description = "call-off self check",
                PrType = ProcurementPrType.RawMaterial
            };

            try
            {
                await prRepo.SaveAsync(pr);

                // Two vendors on one material, plus a second material, so grouping has something to
                // collapse. The trailing space on one name proves the TRIM in the GROUP BY matches
                // the TRIM in the per-material lookup.
                await prRepo.SavePoAsync(NewPo(pr.Id, marker + "-PO1", "Vendor Alpha", (materialA + " ", 100m), (materialB, 40m)));
                await prRepo.SavePoAsync(NewPo(pr.Id, marker + "-PO2", "Vendor Beta", (materialA, 60m)));

                // ---- summaries: one row per material, aggregated in SQL ----
                var all = await callOffs.GetMaterialSummariesAsync();
                var a = Single(all, materialA);
                var b = Single(all, materialB);
                Assert(a.LineCount == 2, $"two POs on one material collapse to one group of 2 lines; got {a.LineCount}");
                Assert(a.TotalOrdered == 160m, $"ordered quantity sums across both POs; got {a.TotalOrdered}");
                Assert(a.TotalCalledOff == 0m, $"nothing called off yet; got {a.TotalCalledOff}");
                Assert(b.LineCount == 1 && b.TotalOrdered == 40m, "the second material is its own group");

                // ---- lines: only that material's, and they agree with the summary ----
                var linesA = await callOffs.GetLinesForMaterialAsync(materialA);
                Assert(linesA.Count == 2, $"lines come back for both POs; got {linesA.Count}");
                Assert(linesA.Sum(l => l.OrderedQuantity) == a.TotalOrdered,
                    "the lines' own total matches the summary the collapsed header displayed");
                Assert(linesA.All(l => l.MaterialName.Trim().Equals(materialA, StringComparison.OrdinalIgnoreCase)),
                    "no other material's lines leak into the group");
                Assert(linesA.Select(l => l.Vendor).Distinct().Count() == 2, "both vendors are present");
                Assert((await callOffs.GetLinesForMaterialAsync(materialB)).Count == 1,
                    "the second material returns only its own line");

                // ---- search: a vendor hit must still surface the material group ----
                var byVendor = await callOffs.GetMaterialSummariesAsync("Vendor Beta");
                var aFiltered = Single(byVendor, materialA);
                Assert(aFiltered.LineCount == 1, $"a vendor search narrows the group to that vendor's line; got {aFiltered.LineCount}");
                Assert(aFiltered.TotalOrdered == 60m, $"and the group total narrows with it; got {aFiltered.TotalOrdered}");
                Assert(!byVendor.Any(s => s.MaterialName.Equals(materialB, StringComparison.OrdinalIgnoreCase)),
                    "a material with no matching vendor drops out entirely");

                // The expanded group must list exactly what the header counted, or the header says
                // one vendor and the body shows two.
                var linesFiltered = await callOffs.GetLinesForMaterialAsync(materialA, "Vendor Beta");
                Assert(linesFiltered.Count == aFiltered.LineCount,
                    $"expanded lines match the filtered summary's count; {linesFiltered.Count} vs {aFiltered.LineCount}");

                // PO1 carries both materials, so a PO-number search surfaces both of its groups.
                var byPo = await callOffs.GetMaterialSummariesAsync(marker + "-PO1");
                Assert(byPo.Count == 2, $"search matches on PO number, across every material on that PO; got {byPo.Count}");
                Assert(Single(byPo, materialA).LineCount == 1, "and narrows each group to that PO's own line");

                Assert((await callOffs.GetMaterialSummariesAsync(marker + "-nope")).Count == 0,
                    "a term matching nothing returns nothing");
                Assert((await callOffs.GetMaterialSummariesAsync("%")).Count == 0,
                    "LIKE wildcards in the term are escaped, not honoured");

                // ---- a logged call-off moves the aggregate the collapsed header shows ----
                var line = linesA.First(l => l.Vendor == "Vendor Beta");
                await callOffs.LogCallOffAsync(new PoItemCallOff
                {
                    PoItemId = line.PoItemId,
                    CallOffDate = DateTime.Today,
                    Quantity = 25m
                });
                var afterLog = Single(await callOffs.GetMaterialSummariesAsync(), materialA);
                Assert(afterLog.TotalCalledOff == 25m, $"the summary picks up a logged call-off; got {afterLog.TotalCalledOff}");
                Assert(afterLog.TotalOrdered == 160m, "and the ordered total is unchanged by it");

                // ---- MaterialGroup: collapsing must release the rows, not keep them ----
                var group = new MaterialGroup(afterLog);
                Assert(group.Count == 0 && !group.LinesLoaded, "a new group starts empty and unloaded");
                Assert(group.TotalOrdered == afterLog.TotalOrdered && group.VendorCountText == "2 vendors",
                    "a collapsed group's header reads from the summary, with no lines loaded");

                var fetched = 0;
                group.LinesRequested = async g =>
                {
                    fetched++;
                    g.SetLines(await callOffs.GetLinesForMaterialAsync(g.MaterialName));
                };
                group.IsExpanded = true;
                for (var i = 0; i < 60 && !group.LinesLoaded; i++) await Task.Delay(50);
                Assert(group.LinesLoaded && group.Count == 2, $"expanding fills the group; got {group.Count} rows");

                group.IsExpanded = false;
                Assert(group.Count == 0 && !group.LinesLoaded,
                    "collapsing drops the rows - a closed group must cost nothing but its header");
                Assert(group.TotalOrdered == afterLog.TotalOrdered,
                    "and the header totals survive the collapse, because they never came from the rows");

                group.IsExpanded = true;
                for (var i = 0; i < 60 && !group.LinesLoaded; i++) await Task.Delay(50);
                Assert(group.Count == 2 && fetched == 2, "re-expanding refetches rather than serving stale rows");

                group.ApplyCalledOffDelta(10m);
                Assert(group.TotalCalledOff == 35m, $"a logged call-off adjusts the header in place; got {group.TotalCalledOff}");

                Report("PASS");
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message);
            }
            finally
            {
                try { await prRepo.DeleteAsync(pr.Id); } catch { }
            }
        }

        private static PurchaseOrder NewPo(Guid prId, string poNo, string vendor, params (string Name, decimal Qty)[] items)
        {
            var po = new PurchaseOrder { PrId = prId, PoNo = poNo, Vendor = vendor };
            var sort = 0;
            foreach (var (name, qty) in items)
            {
                po.Items.Add(new PurchaseOrderItem
                {
                    PoId = po.Id,
                    ItemName = name,
                    Quantity = qty,
                    Unit = "MT",
                    SortOrder = sort++
                });
            }
            return po;
        }

        private static MaterialGroupSummary Single(List<MaterialGroupSummary> all, string material)
        {
            var hits = all.Where(s => s.MaterialName.Equals(material, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert(hits.Count == 1, $"expected exactly one group for '{material}'; got {hits.Count}");
            return hits[0];
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("CallOffSelfCheck failed: " + what);
        }

        /// <summary>A GUI process has nowhere useful to print, so the result goes next to the
        /// database, same as the other data-layer checks.</summary>
        private static void Report(string result)
        {
            System.Diagnostics.Debug.WriteLine("CallOffSelfCheck: " + result);
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(DatabaseConstants.DatabaseDirectory, "calloff-selfcheck.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {result}{Environment.NewLine}");
            }
            catch
            {
                // A diagnostic must never be the thing that breaks the run.
            }
        }
    }
}
