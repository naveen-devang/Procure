using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.PageModels;
using Procure.Services;
using Procure.Utilities;

namespace Procure.Data
{
    /// <summary>
    /// Proves a requisition's status holds where a person put it: a PCR, an approval date, a deleted order
    /// or a status edit may move it along the pipeline, but never past Closed, On Hold or Cancelled, and
    /// never leave "PO Raised" on a requisition with no order. Also checks that Save in Settings does not
    /// write the database folder down. Drives the real repository and board page model against whatever
    /// database it is pointed at - run it on a COPY of the 20,000-PR database:
    ///
    ///     PROCURE_STATUS_PROBE=1  PROCURE_DB_DIR=C:\...\copy-of-procure-20k  Procure.exe
    ///
    /// Everything it creates is marked and deleted at the end. The real settings.json is backed up and
    /// put back. Result: status-probe.log next to the database, and the crash log.
    /// </summary>
    internal static class StatusIntegrityProbe
    {
        private static readonly StringBuilder Log = new();
        private static int _checks, _fails;
        private static readonly List<Guid> Created = new();
        private static string _run = "";
        private static IPurchaseRequisitionRepository _repo = null!;

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok) _fails++;
            Log.AppendLine((ok ? "  ok    " : "  FAIL  ") + what);
        }

        private static void Section(string title) { Log.AppendLine(); Log.AppendLine(title); }

        private static async Task<bool> Until(Func<bool> condition, int timeoutMs = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                await Task.Delay(15).ConfigureAwait(true);
            }
            return condition();
        }

        private static async Task Settled(PrListPageModel vm)
        {
            await Task.Delay(40).ConfigureAwait(true);
            await Until(() => vm.IsReloadIdleForTest && !vm.IsSearchPending && !vm.IsBusy).ConfigureAwait(true);
        }

        // ---- fixtures ----

        private static async Task<PurchaseRequisition> NewPr(string tag, string status)
        {
            var pr = new PurchaseRequisition
            {
                Id = Guid.NewGuid(), PrNo = $"SP{_run}{tag}", Description = "status probe " + tag, Requestor = "probe",
                Plant = ProcurementPlant.RW01, PrType = ProcurementPrType.StoresAndSpares, Status = status,
            };
            Created.Add(pr.Id);
            await _repo.SaveAsync(pr).ConfigureAwait(true);
            return pr;
        }

        private static async Task<RequestForQuotation> NewRfq(PurchaseRequisition pr, string tag, bool received)
        {
            var rfq = new RequestForQuotation
            {
                Id = Guid.NewGuid(), PrId = pr.Id, RfqNo = $"{pr.PrNo}-{tag}", Vendor = "probe vendor " + tag, Currency = "AED", VatType = "5%",
                Status = received ? RfqStatus.QuoteReceived : RfqStatus.Sent,
                SentDate = DateTime.Today, QuoteReceivedDate = received ? DateTime.Today : null,
            };
            await _repo.SaveRfqAsync(rfq).ConfigureAwait(true);
            return rfq;
        }

        private static async Task<PurchaseOrder> NewPo(PurchaseRequisition pr, string tag, string status)
        {
            var po = new PurchaseOrder
            {
                Id = Guid.NewGuid(), PrId = pr.Id, PoNo = $"{pr.PrNo}-{tag}", Vendor = "probe vendor", Currency = "AED", VatType = "5%",
                Value = 100m, Status = status, Date = DateTime.Today,
            };
            await _repo.SavePoAsync(po).ConfigureAwait(true);
            return po;
        }

        private static async Task<PriceComparisonRequest> NewPcr(PurchaseRequisition pr, bool approved)
        {
            var pcr = new PriceComparisonRequest { Id = Guid.NewGuid(), PrId = pr.Id, PcrNo = "PCR-" + pr.PrNo, CreatedAt = DateTime.Now };
            pcr.EnsureDefaultApprovals(new List<string> { "Procurement Manager", "Finance Controller" });
            if (approved) foreach (var a in pcr.Approvals) a.ReceivedDate = DateTime.Today;
            await _repo.SavePcrAsync(pcr).ConfigureAwait(true);
            return pcr;
        }

        private static async Task<string> StatusOf(Guid id) => (await _repo.GetByIdsAsync(new[] { id }).ConfigureAwait(true)).Single().Status;

        public static async Task RunAsync(IServiceProvider services)
        {
            string? settingsBackup = null;
            var settingsPath = Path.Combine(AppPaths.AppData, "settings.json");
            var settingsExisted = false;
            try
            {
                var vm = services.GetRequiredService<PrListPageModel>();
                _repo = services.GetRequiredService<IPurchaseRequisitionRepository>();
                var undo = services.GetRequiredService<UndoDeleteService>();
                _run = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
                Log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  status integrity probe");

                // ------------------------------------------------------ 1 deleting an order or a quote
                Section("1. Deleting an order or quote lets the status fall back, in the database");
                {
                    var pr = await NewPr("A01", ProcurementStatus.PoRaised); await NewRfq(pr, "r", true); await NewPcr(pr, true); var po = await NewPo(pr, "p", PoStatus.Raised);
                    await _repo.DeletePoAsync(po.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.PcrApproved, "last order gone, PCR approved -> PCR Approved");
                }
                {
                    var pr = await NewPr("A02", ProcurementStatus.PoRaised); await NewRfq(pr, "r", true); await NewPcr(pr, false); var po = await NewPo(pr, "p", PoStatus.Raised);
                    await _repo.DeletePoAsync(po.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.PcrSubmitted, "last order gone, PCR still pending -> PCR Submitted");
                }
                {
                    var pr = await NewPr("A03", ProcurementStatus.PoRaised); await NewRfq(pr, "r", true); var po = await NewPo(pr, "p", PoStatus.Raised);
                    await _repo.DeletePoAsync(po.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.QuotesReceived, "last order gone, quote in -> Quotes Received");
                }
                {
                    var pr = await NewPr("A04", ProcurementStatus.PoRaised); await NewRfq(pr, "r", false); var po = await NewPo(pr, "p", PoStatus.Raised);
                    await _repo.DeletePoAsync(po.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.RfqSent, "last order gone, quote outstanding -> RFQ Sent");
                }
                {
                    var pr = await NewPr("A05", ProcurementStatus.PoRaised); var po = await NewPo(pr, "p", PoStatus.Raised);
                    await _repo.DeletePoAsync(po.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.PrRaised, "the only thing it had was the order -> PR Raised");
                }
                {
                    var pr = await NewPr("A06", ProcurementStatus.PartiallyDelivered); await NewPo(pr, "p1", PoStatus.Delivered); var po2 = await NewPo(pr, "p2", PoStatus.Raised);
                    await _repo.DeletePoAsync(po2.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.Delivered, "the undelivered order deleted, the rest delivered -> Delivered");
                }
                {
                    var pr = await NewPr("A07", ProcurementStatus.PoRaised); var po1 = await NewPo(pr, "p1", PoStatus.Raised); await NewPo(pr, "p2", PoStatus.Raised);
                    await _repo.DeletePoAsync(po1.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.PoRaised, "one of two orders deleted -> still PO Raised");
                }
                foreach (var (tag, status) in new[] { ("A08", ProcurementStatus.Closed), ("A09", ProcurementStatus.OnHold), ("A10", ProcurementStatus.Cancelled) })
                {
                    var pr = await NewPr(tag, status); var po = await NewPo(pr, "p", PoStatus.Raised);
                    await _repo.DeletePoAsync(po.Id);
                    Check(await StatusOf(pr.Id) == status, $"deleting its order leaves a {status} requisition {status}");
                }
                {
                    var pr = await NewPr("B01", ProcurementStatus.RfqSent); var rfq = await NewRfq(pr, "r", false);
                    await _repo.DeleteRfqAsync(rfq.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.PrRaised, "last quote deleted -> PR Raised");
                }
                {
                    var pr = await NewPr("B02", ProcurementStatus.RfqSent); await NewRfq(pr, "r1", true); var rfq2 = await NewRfq(pr, "r2", false);
                    await _repo.DeleteRfqAsync(rfq2.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.QuotesReceived, "the outstanding quote deleted, the rest in -> Quotes Received");
                }
                {
                    var pr = await NewPr("B03", ProcurementStatus.PoRaised); var rfq = await NewRfq(pr, "r", true); await NewPo(pr, "p", PoStatus.Raised);
                    await _repo.DeleteRfqAsync(rfq.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.PoRaised, "deleting a quote does not move an ordered requisition");
                }
                {
                    var pr = await NewPr("B04", ProcurementStatus.Delivered); var rfq = await NewRfq(pr, "r", true);
                    await _repo.DeleteRfqAsync(rfq.Id);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.Delivered, "a hand-set Delivered with no orders is not second-guessed by a quote delete");
                }

                // the board path: Undo window, then commit
                Section("2. Through the board: delete with Undo, then the card shows the fallen-back status");
                {
                    var pr = await NewPr("C01", ProcurementStatus.PoRaised); await NewRfq(pr, "r", true); var po = await NewPo(pr, "p", PoStatus.Raised);
                    vm.SearchAnyOf(pr.PrNo); await Settled(vm);
                    var card = vm.FilteredPrs.FirstOrDefault(p => p.Id == pr.Id);
                    Check(card is not null && card.Pos.Count == 1, "the test requisition is on the board with its order");
                    if (card is not null)
                    {
                        var live = card.Pos[0];
                        await vm.DeleteChildWithUndoAsync(live, p => p.Id, pr.Id, DeleteKind.Po, "probe", x => x.Pos, () => { });
                        Check(card.Pos.Count == 0 && card.Status == ProcurementStatus.PoRaised, "during the Undo window the order is hidden and the status has not changed yet");
                        undo.Undo();
                        Check(card.Pos.Count == 1 && await StatusOf(pr.Id) == ProcurementStatus.PoRaised, "Undo puts the order back and nothing was written");

                        await vm.DeleteChildWithUndoAsync(live, p => p.Id, pr.Id, DeleteKind.Po, "probe", x => x.Pos, () => { });
                        await undo.CommitNowAsync();
                        await Settled(vm);
                        await Until(() => card.Status == ProcurementStatus.QuotesReceived, 5000);
                        Check(await StatusOf(pr.Id) == ProcurementStatus.QuotesReceived, "committed: the database says Quotes Received");
                        Check(card.Status == ProcurementStatus.QuotesReceived, "and the card on the board says it too, without a refresh");
                    }
                }

                // ------------------------------------------------------ 3 comparisons
                Section("3. A PCR never moves a requisition that is ordered, finished or parked");
                foreach (var (tag, status, expect) in new[]
                {
                    ("D01", ProcurementStatus.PrRaised, ProcurementStatus.PcrSubmitted),
                    ("D02", ProcurementStatus.QuotesReceived, ProcurementStatus.PcrSubmitted),
                    ("D03", ProcurementStatus.PoRaised, ProcurementStatus.PoRaised),
                    ("D04", ProcurementStatus.PartiallyDelivered, ProcurementStatus.PartiallyDelivered),
                    ("D05", ProcurementStatus.Delivered, ProcurementStatus.Delivered),
                    ("D06", ProcurementStatus.Closed, ProcurementStatus.Closed),
                    ("D07", ProcurementStatus.OnHold, ProcurementStatus.OnHold),
                    ("D08", ProcurementStatus.Cancelled, ProcurementStatus.Cancelled),
                })
                {
                    var pr = await NewPr(tag, status);
                    var loaded = (await _repo.GetByIdsAsync(new[] { pr.Id })).Single();
                    await vm.CreatePcrForPrAsync(loaded);
                    Check(await StatusOf(pr.Id) == expect, $"creating a PCR on a {status} requisition -> {expect}");
                }

                Section("4. Signing the last approval");
                foreach (var (tag, status, expect) in new[]
                {
                    ("E01", ProcurementStatus.PcrSubmitted, ProcurementStatus.PcrApproved),
                    ("E02", ProcurementStatus.PoRaised, ProcurementStatus.PoRaised),
                    ("E03", ProcurementStatus.Delivered, ProcurementStatus.Delivered),
                    ("E04", ProcurementStatus.Closed, ProcurementStatus.Closed),
                    ("E05", ProcurementStatus.OnHold, ProcurementStatus.OnHold),
                })
                {
                    var pr = await NewPr(tag, status); await NewPcr(pr, false);
                    vm.SearchAnyOf(pr.PrNo); await Settled(vm);
                    var card = vm.FilteredPrs.FirstOrDefault(p => p.Id == pr.Id);
                    if (card?.Pcr is null) { Check(false, $"{tag}: requisition with PCR is on the board"); continue; }
                    foreach (var a in card.Pcr.Approvals.ToList())
                    {
                        a.ReceivedDate = DateTime.Today;
                        await vm.HandleApprovalDateChangedAsync(a);
                    }
                    await Until(() => card.Status == expect, 3000);
                    await Task.Delay(150);
                    Check(await StatusOf(pr.Id) == expect, $"all approvals received on a {status} requisition -> {expect}");
                }

                Section("5. Saving the approval stages");
                foreach (var (tag, status, expect) in new[]
                {
                    ("F01", ProcurementStatus.PcrSubmitted, ProcurementStatus.PcrApproved),
                    ("F02", ProcurementStatus.Delivered, ProcurementStatus.Delivered),
                    ("F03", ProcurementStatus.Closed, ProcurementStatus.Closed),
                    ("F04", ProcurementStatus.PoRaised, ProcurementStatus.PoRaised),
                })
                {
                    var pr = await NewPr(tag, status); await NewPcr(pr, true);
                    vm.SearchAnyOf(pr.PrNo); await Settled(vm);
                    var card = vm.FilteredPrs.FirstOrDefault(p => p.Id == pr.Id);
                    if (card?.Pcr is null) { Check(false, $"{tag}: requisition with PCR is on the board"); continue; }
                    await vm.OpenApprovalConfigModalCommand.ExecuteAsync(card);
                    await vm.SaveApprovalConfigModalCommand.ExecuteAsync(null);
                    Check(await StatusOf(pr.Id) == expect, $"saving the stages of a fully approved {status} requisition -> {expect}");
                }

                // ------------------------------------------------------ 6 orders and quotes edited in place
                Section("6. Setting an order or quote back");
                {
                    var pr = await NewPr("G01", ProcurementStatus.PoRaised); var po = await NewPo(pr, "p", PoStatus.Raised);
                    vm.SearchAnyOf(pr.PrNo); await Settled(vm);
                    // A re-read after the first edit may replace the card's order objects, so each step
                    // takes the one the screen would be holding now.
                    PurchaseOrder Live() => vm.FilteredPrs.First(p => p.Id == pr.Id).Pos.First();
                    await vm.UpdatePoStatusDirectAsync(Live(), PoStatus.Delivered);
                    await Settled(vm);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.Delivered, "the only order delivered -> Delivered");
                    await vm.UpdatePoStatusDirectAsync(Live(), PoStatus.Raised);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.PoRaised, "the order set back to Raised -> PO Raised again");
                }
                foreach (var (tag, status) in new[] { ("G02", ProcurementStatus.Closed), ("G03", ProcurementStatus.OnHold), ("G04", ProcurementStatus.Cancelled) })
                {
                    var pr = await NewPr(tag, status); await NewPo(pr, "p", PoStatus.Raised);
                    vm.SearchAnyOf(pr.PrNo); await Settled(vm);
                    var card = vm.FilteredPrs.First(p => p.Id == pr.Id);
                    await vm.UpdatePoStatusDirectAsync(card.Pos.First(), PoStatus.Delivered);
                    Check(await StatusOf(pr.Id) == status, $"an order delivered on a {status} requisition leaves it {status}");
                }
                {
                    var pr = await NewPr("G05", ProcurementStatus.QuotesReceived); await NewRfq(pr, "r1", true);
                    vm.SearchAnyOf(pr.PrNo); await Settled(vm);
                    RequestForQuotation LiveRfq() => vm.FilteredPrs.First(p => p.Id == pr.Id).Rfqs.First();
                    await vm.UpdateRfqStatusDirectAsync(LiveRfq(), RfqStatus.Sent);
                    await Settled(vm);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.RfqSent, "the only quote set back to Sent -> RFQ Sent");
                    await vm.UpdateRfqStatusDirectAsync(LiveRfq(), RfqStatus.QuoteReceived);
                    Check(await StatusOf(pr.Id) == ProcurementStatus.QuotesReceived, "and received again -> Quotes Received");
                }

                // ------------------------------------------------------ 7 settings
                Section("7. Save in Settings does not write the database folder down");
                {
                    var effective = DatabaseConstants.DatabaseDirectory;
                    settingsExisted = File.Exists(settingsPath);
                    settingsBackup = settingsExisted ? await File.ReadAllTextAsync(settingsPath) : null;
                    var folderBefore = settingsBackup is null ? null : JsonDocumentValue(settingsBackup, "DatabaseDirectory");

                    var settings = services.GetRequiredService<SettingsPageModel>();
                    await settings.SaveSettingsCommand.ExecuteAsync(null);

                    var after = File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath) : "{}";
                    var savedFolder = JsonDocumentValue(after, "DatabaseDirectory");
                    Check(savedFolder == folderBefore, $"after Save the saved database folder is unchanged ('{folderBefore ?? "none"}'), not the folder in use ('{effective}'); found '{savedFolder ?? "none"}'");
                    Check(services.GetRequiredService<ISettingsService>().LastSaveError is null, "and the save itself reported no error");
                }
            }
            catch (Exception ex)
            {
                _fails++;
                Log.AppendLine("  THREW " + ex);
            }
            finally
            {
                try
                {
                    foreach (var id in Created) { try { await _repo.DeleteAsync(id).ConfigureAwait(true); } catch { /* best effort */ } }
                    if (settingsExisted && settingsBackup is not null) await File.WriteAllTextAsync(settingsPath, settingsBackup).ConfigureAwait(true);
                    else if (!settingsExisted && File.Exists(settingsPath)) File.Delete(settingsPath);
                }
                catch (Exception ex) { Log.AppendLine("  cleanup threw " + ex.Message); }

                var summary = _fails == 0 ? $"PASS  {_checks} checks" : $"FAIL  {_fails} of {_checks} checks failed";
                Log.Insert(0, "STATUS INTEGRITY PROBE " + summary + Environment.NewLine);
                CrashLog.Write("STATUS INTEGRITY PROBE " + summary + Environment.NewLine + Log);
                try { File.WriteAllText(Path.Combine(DatabaseConstants.DatabaseDirectory, "status-probe.log"), Log.ToString()); }
                catch { /* the crash log already has it */ }
            }
        }

        private static string? JsonDocumentValue(string json, string key)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }
    }
}
