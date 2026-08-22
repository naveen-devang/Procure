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
    /// SearchBlob is derived data kept in sync by hand: every write path that touches a PR, its items,
    /// its RFQs or its POs has to end in RefreshSearchBlobAsync. Miss one and nothing breaks loudly -
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
            cmd.CommandText = DatabaseConstants.SqlStaleSearchBlobCount + ";";
            var stale = Convert.ToInt32(await cmd.ExecuteScalarAsync());

            // Throws rather than Debug.Assert: an assert only raises a dialog, which a headless or
            // unattended run never sees, so the failure would pass silently.
            if (stale != 0)
                throw new InvalidOperationException(
                    $"{stale} PR(s) have a stale SearchBlob {step}. A write path changed a PR, item, " +
                    "RFQ or PO without calling RefreshSearchBlobAsync, so search will not find it.");
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
