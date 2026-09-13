using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;

namespace Procure.Data
{
    /// <summary>
    /// The one runnable check behind the denormalised search column.
    ///
    /// The search index is derived data kept in sync by hand: every write path that touches a PR, its
    /// items, its RFQs or its POs has to end in RefreshSearchIndexAsync. Miss one and nothing breaks loudly -
    /// search just quietly stops finding whatever that path changed. This asserts the invariant that
    /// catches it: no PR's stored blob may differ from what the expression would produce right now.
    ///
    /// Run it by launching a Debug build with PROCURE_SELFCHECK=1 set. It exercises a PR through the
    /// real repository - save, add an RFQ, add a PO, rename the item, delete the RFQ - re-checking
    /// after each step, then removes the PR it created. It writes to the live database, so it is Debug
    /// only and never runs unless you ask for it.
    /// </summary>
    internal static class DatabaseSelfCheck
    {
        public static async Task RunAsync(SqliteDatabase db, IPurchaseRequisitionRepository repo)
        {
            var marker = "selfcheck-" + Guid.NewGuid().ToString("N")[..8];
            var pr = new PurchaseRequisition
            {
                PrNo = marker,
                Description = "self check",
                Items = { new PrItem { ItemName = marker + "-item", Quantity = 1 } }
            };

            try
            {
                await repo.SaveAsync(pr);
                await AssertFreshAsync(db, "after SaveAsync");
                await AssertFindsAsync(repo, marker + "-item", "item name is searchable");

                var rfq = new RequestForQuotation { PrId = pr.Id, RfqNo = marker + "-rfq", Vendor = marker + "-vendor" };
                await repo.SaveRfqAsync(rfq);
                await AssertFreshAsync(db, "after SaveRfqAsync");
                await AssertFindsAsync(repo, marker + "-vendor", "vendor name is searchable");

                var po = new PurchaseOrder { PrId = pr.Id, PoNo = marker + "-po", Vendor = marker + "-vendor" };
                await repo.SavePoAsync(po);
                await AssertFreshAsync(db, "after SavePoAsync");
                await AssertFindsAsync(repo, marker + "-po", "PO number is searchable");

                // Transport allocations: a line whose quantity is split across two contracts has to
                // survive the round trip, and a whole-order PO must own none.
                po.TransportMode = TransportModes.Line;
                po.Items.Add(new PurchaseOrderItem
                {
                    PoId = po.Id,
                    ItemName = marker + "-line",
                    Quantity = 42,
                    Transports =
                    {
                        new PoItemTransport { Quantity = 18, ContractNumber = marker + "-tc-a", TransporterName = "A", RatePerUnit = 12 },
                        new PoItemTransport { Quantity = 24, ContractNumber = marker + "-tc-b", TransporterName = "B", RatePerUnit = 15 },
                    }
                });
                await repo.SavePoAsync(po);
                await AssertTransportAsync(repo, pr.Id, 2, 18m * 12m + 24m * 15m, "a split line round-trips");

                // Switching back to whole-order has to clear them, or the two would disagree about
                // which is the real contract.
                po.TransportMode = TransportModes.Order;
                await repo.SavePoAsync(po);
                await AssertTransportAsync(repo, pr.Id, 0, 0m, "whole-order mode owns no allocations");

                // The case a stale blob shows up as: the row changes, the blob does not.
                pr.Items[0].ItemName = marker + "-renamed";
                await repo.SaveAsync(pr);
                await AssertFreshAsync(db, "after renaming an item");
                await AssertFindsAsync(repo, marker + "-renamed", "a renamed item is searchable");

                await repo.DeleteRfqAsync(rfq.Id);
                await AssertFreshAsync(db, "after DeleteRfqAsync");

                Report("PASS");
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message);
                throw;
            }
            finally
            {
                await repo.DeleteAsync(pr.Id);
            }
        }

        /// <summary>A GUI process has nowhere useful to print, so the result goes next to the database
        /// where you can actually read it.</summary>
        private static void Report(string result)
        {
            Debug.WriteLine("DatabaseSelfCheck: " + result);
            try
            {
                File.WriteAllText(
                    Path.Combine(DatabaseConstants.DatabaseDirectory, "selfcheck.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {result}{Environment.NewLine}");
            }
            catch
            {
                // A diagnostic must never be the thing that breaks the run.
            }
        }

        private static async Task AssertFreshAsync(SqliteDatabase db, string step)
        {
            using var connection = db.CreateConnection();
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = DatabaseConstants.SqlStaleSearchRowCount;
            var stale = Convert.ToInt32(await cmd.ExecuteScalarAsync());

            // Throws rather than Debug.Assert: an assert only raises a dialog, which a headless or
            // unattended run never sees, so the failure would pass silently.
            if (stale != 0)
                throw new InvalidOperationException(
                    $"{stale} PR(s) have a stale search index row {step}. A write path changed a PR, " +
                    "item, RFQ or PO without calling RefreshSearchIndexAsync, so search will not find it.");

            // The material aggregates are derived the same way and carry the same hazard: a write
            // path that changes a PO item, a call-off or a PR's type without refreshing them leaves
            // the Raw & Packing tab showing a stale count or balance, with nothing else going wrong.
            // One index row per requisition. A write path that changes a PR without re-indexing it
            // makes that PR unfindable, which is silent - nobody notices a result that is missing.
            using (var ftsCmd = connection.CreateCommand())
            {
                ftsCmd.CommandText = DatabaseConstants.SqlSearchIndexDriftCount;
                var drift = Convert.ToInt32(await ftsCmd.ExecuteScalarAsync());
                if (drift != 0)
                    throw new InvalidOperationException(
                        $"The search index is {Math.Abs(drift)} row(s) {(drift > 0 ? "short of" : "ahead of")} " +
                        $"the requisitions {step}. A write path changed a PR without re-indexing it, " +
                        "so it will not be findable.");
            }

            using var aggCmd = connection.CreateCommand();
            aggCmd.CommandText = DatabaseConstants.SqlStaleMaterialAggregateCount;
            var staleMaterials = Convert.ToInt32(await aggCmd.ExecuteScalarAsync());

            if (staleMaterials != 0)
                throw new InvalidOperationException(
                    $"{staleMaterials} material aggregate row(s) disagree with the live data {step}. " +
                    "A write path changed a PO item, a call-off or a PR's type without going through " +
                    "MaterialAggregateMaintenance, so Raw & Packing will show stale figures.");
        }

        /// <summary>Reads the PR back and checks the PO line's transport allocations - count and
        /// money - rather than trusting that what was written is what comes back.</summary>
        private static async Task AssertTransportAsync(
            IPurchaseRequisitionRepository repo, Guid prId, int expectedCount, decimal expectedTotal, string what)
        {
            var fresh = (await repo.GetByIdsAsync(new[] { prId }).ConfigureAwait(false)).FirstOrDefault();
            var line = fresh?.Pos?.SelectMany(p => p.Items).FirstOrDefault(i => i.Transports.Count > 0 || expectedCount == 0);
            var actualCount = line?.Transports.Count ?? 0;
            var actualTotal = line?.TransportTotal ?? 0m;

            if (actualCount != expectedCount || actualTotal != expectedTotal)
                throw new InvalidOperationException(
                    $"Transport allocations wrong: {what}. Expected {expectedCount} row(s) totalling {expectedTotal}, " +
                    $"got {actualCount} totalling {actualTotal}.");
        }

        private static async Task AssertFindsAsync(IPurchaseRequisitionRepository repo, string term, string what)
        {
            var page = await repo.GetPageAsync(new PrQuery(term, null, false, false, false, 10, 5, 0, 10));

            if (page.TotalCount != 1)
                throw new InvalidOperationException(
                    $"searching '{term}' returned {page.TotalCount} PRs, expected 1 - {what} is broken.");
        }
    }
}
