using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.PageModels;
using Procure.Services.Export;

namespace Procure.Data
{
    /// <summary>PROCURE_SERVICE_SELFCHECK=1: the Service Entry register's SQL against the real schema -
    /// the stage the table derives matches the model's, filters and totals add up, the export opens.
    /// Writes rows with Sr Nos from 9,000,000 up and deletes them after, so run it on a test database.</summary>
    public static class ServiceEntrySelfCheck
    {
        public static async Task RunAsync(IServiceProvider services)
        {
            var repo = services.GetRequiredService<ServiceEntryRepository>();
            var made = new System.Collections.Generic.List<Guid>();
            static void Check(bool ok, string what)
            {
                SelfCheckLog.Write((ok ? "PASS " : "FAIL ") + "ServiceEntry: " + what);
                if (!ok) throw new InvalidOperationException("ServiceEntry: " + what);
            }

            try
            {
                var today = DateTime.Today;
                var po = "SELFCHECK-PO-" + Guid.NewGuid().ToString("N")[..6];
                ServiceEntry Make(int sr, int stage, decimal amt, int daysAgo)
                {
                    var d = today.AddDays(-daysAgo);
                    var e = new ServiceEntry
                    {
                        SrNo = 9_000_000 + sr, PoNo = po, PoAmount = 1000m, Vendor = "Selfcheck 100%_Vendor",
                        Description = "AMC", InvoiceDate = d, InvoiceNo = "INV" + sr, InvoiceAmount = amt,
                        TechHandoverDate = stage >= 1 ? d : null,
                        SapSeDate = stage >= 2 ? d : null, ServiceEntryNo = stage >= 2 ? "SE" + sr : null,
                        AccountHandoverDate = stage >= 3 ? d : null,
                    };
                    made.Add(e.Id);
                    return e;
                }

                var before = await repo.GetSummaryAsync(10);
                var rows = new[] { Make(1, 0, 100m, 1), Make(2, 1, 200m, 30), Make(3, 2, 300m, 2), Make(4, 3, 450m, 0) };
                foreach (var r in rows) await repo.SaveAsync(r);

                var mine = await repo.GetPageAsync("all", po, 0, 50);
                Check(mine.Count == 4, "search by PO finds all four");
                foreach (var r in rows)
                    Check(mine.Single(m => m.Id == r.Id).Stage == r.Stage, $"table stage = model stage ({r.Stage})");
                Check(mine.Select(m => m.SrNo).SequenceEqual(mine.Select(m => m.SrNo).OrderByDescending(x => x)), "newest Sr No first");

                for (var s = 0; s < 4; s++)
                    Check((await repo.GetPageAsync(s.ToString(), po, 0, 50)).Single().Id == rows[s].Id, $"stage {s} tab shows only its row");
                Check((await repo.GetPageAsync("open", po, 0, 50)).Count == 3, "open hides the finished one");
                Check((await repo.GetPageAsync("all", "100%_v", 0, 50)).Count == 4, "% and _ in a search are literal");
                Check((await repo.GetPageAsync("all", "9000003", 0, 50)).Any(e => e.Id == rows[2].Id), "search by Sr No");   // other rows may contain the digits

                var after = await repo.GetSummaryAsync(10);
                Check(after.OpenCount - before.OpenCount == 3, "summary: three more open");
                Check(after.LateCount - before.LateCount == 1, "summary: the 30-day one is late at 10 days");
                Check(after.OpenAmount - before.OpenAmount == 600m, "summary: open amount");

                var (count, total) = await repo.GetPoBilledAsync(po.ToLowerInvariant(), rows[0].Id);
                Check(count == 3 && total == 950m, "billed on the PO, ignoring case, leaving out the one being edited");
                Check(await repo.SrNoTakenAsync(9_000_001, Guid.NewGuid()) && !await repo.SrNoTakenAsync(9_000_001, rows[0].Id), "Sr No taken");
                Check(await repo.NextSrNoAsync() > 9_000_004, "next Sr No follows the highest");
                Check((await repo.FindVendorsAsync("selfcheck 100%")).Contains("Selfcheck 100%_Vendor"), "vendor suggestion");

                Check(ServiceEntryPageModel.ParseMoney("AED 3,281.28") == 3281.28m && ServiceEntryPageModel.ParseMoney("abc") is null, "money parsing");

                var bytes = ServiceEntryExcelExporter.Generate(mine);
                using var zip = new ZipArchive(new MemoryStream(bytes));
                using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                var xml = System.Xml.Linq.XDocument.Parse(sheet.ReadToEnd());
                Check(xml.Descendants().Count(e => e.Name.LocalName == "row") == 5, "export: header + four rows, well-formed");
            }
            catch (Exception ex)
            {
                SelfCheckLog.Write("FAIL ServiceEntry: " + ex.Message);
            }
            finally
            {
                foreach (var id in made) { try { await repo.DeleteAsync(id); } catch { } }
                SelfCheckLog.Write("ServiceEntry self-check done");
            }
        }
    }
}
