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
                var db = services.GetRequiredService<SqliteDatabase>();
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

                // ------------------------------------------------------ 8 historical price currency
                Section("8. A historical price keeps its currency through Edit PR");
                {
                    var pr = await NewPr("H01", ProcurementStatus.PrRaised);
                    pr.Items.Add(new PrItem { Id = Guid.NewGuid(), PrId = pr.Id, ItemName = "probe usd item", Quantity = 2, Unit = "pcs", EstimatedUnitPrice = 86.75m, EstimatedCurrency = "USD", SortOrder = 0 });
                    pr.Items.Add(new PrItem { Id = Guid.NewGuid(), PrId = pr.Id, ItemName = "probe eur item", Quantity = 1, Unit = "pcs", EstimatedUnitPrice = 12m, EstimatedCurrency = "EUR", SortOrder = 1 });
                    pr.Items.Add(new PrItem { Id = Guid.NewGuid(), PrId = pr.Id, ItemName = "probe aed item", Quantity = 1, Unit = "pcs", EstimatedUnitPrice = 5m, SortOrder = 2 });
                    await _repo.SaveAsync(pr);

                    // an open quote that carries the requisition's prices as its Last price
                    var rfq = new RequestForQuotation { Id = Guid.NewGuid(), PrId = pr.Id, RfqNo = pr.PrNo + "-r", Vendor = "probe vendor", Currency = "AED", VatType = "5%", Status = RfqStatus.Sent, SentDate = DateTime.Today };
                    foreach (var i in pr.Items)
                        rfq.Items.Add(new RfqItem { Id = Guid.NewGuid(), RfqId = rfq.Id, PrItemId = i.Id, ItemName = i.ItemName, Quantity = i.Quantity, Unit = i.Unit, IsQuoted = true, LastPrice = i.EstimatedUnitPrice, LastPriceCurrency = i.EstimatedCurrency, SortOrder = i.SortOrder });
                    await _repo.SaveRfqAsync(rfq);

                    vm.SearchAnyOf(pr.PrNo); await Settled(vm);
                    var card = vm.FilteredPrs.First(p => p.Id == pr.Id);
                    await vm.OpenEditPrModalCommand.ExecuteAsync(card);
                    var open = vm.EditingPrItems.ToDictionary(i => i.ItemName, i => i.EstimatedPriceText);
                    Check(open["probe usd item"] == "86.75 USD" && open["probe eur item"] == "12 EUR" && open["probe aed item"] == "5 AED",
                          $"the box reopens as '{open["probe usd item"]}', '{open["probe eur item"]}', '{open["probe aed item"]}'");

                    // save without touching a price
                    await vm.SavePrModalCommand.ExecuteAsync(null); await Settled(vm);
                    var saved = (await _repo.GetByIdsAsync(new[] { pr.Id })).Single().Items.ToDictionary(i => i.ItemName);
                    Check(saved["probe usd item"].EstimatedCurrency == "USD" && saved["probe usd item"].EstimatedUnitPrice == 86.75m
                          && saved["probe eur item"].EstimatedCurrency == "EUR" && saved["probe eur item"].EstimatedUnitPrice == 12m,
                          $"saving an edit that touched no price keeps USD and EUR (found {saved["probe usd item"].EstimatedCurrency}, {saved["probe eur item"].EstimatedCurrency})");

                    // new values: a new price in the same currency, then a currency-only correction
                    card = vm.FilteredPrs.First(p => p.Id == pr.Id);
                    await vm.OpenEditPrModalCommand.ExecuteAsync(card);
                    vm.EditingPrItems.First(i => i.ItemName == "probe usd item").EstimatedPriceText = "90";
                    vm.EditingPrItems.First(i => i.ItemName == "probe eur item").EstimatedPriceText = "12 gbp";
                    await vm.SavePrModalCommand.ExecuteAsync(null); await Settled(vm);
                    var edited = (await _repo.GetByIdsAsync(new[] { pr.Id })).Single().Items.ToDictionary(i => i.ItemName);
                    Check(edited["probe usd item"].EstimatedUnitPrice == 90m && edited["probe usd item"].EstimatedCurrency == "USD",
                          $"a bare new price keeps the currency: 90 {edited["probe usd item"].EstimatedCurrency}");
                    Check(edited["probe eur item"].EstimatedUnitPrice == 12m && edited["probe eur item"].EstimatedCurrency == "GBP",
                          $"correcting only the currency saves: 12 {edited["probe eur item"].EstimatedCurrency}");

                    var quote = (await _repo.GetByIdsAsync(new[] { pr.Id })).Single().Rfqs.Single().Items.ToDictionary(i => i.ItemName);
                    Check(quote["probe usd item"].LastPrice == 90m && quote["probe usd item"].LastPriceCurrency == "USD",
                          $"the open quote's Last price follows: 90 {quote["probe usd item"].LastPriceCurrency}");
                    Check(quote["probe eur item"].LastPrice == 12m && quote["probe eur item"].LastPriceCurrency == "GBP",
                          $"and follows a currency-only change: 12 {quote["probe eur item"].LastPriceCurrency}");
                    Check(quote["probe aed item"].LastPrice == 5m, "an untouched line is left alone");

                    // a line added in the edit reaches the open quote with its currency
                    card = vm.FilteredPrs.First(p => p.Id == pr.Id);
                    await vm.OpenEditPrModalCommand.ExecuteAsync(card);
                    vm.AddEditingPrItemCommand.Execute(null);
                    var added = vm.EditingPrItems.Last();
                    added.ItemName = "probe new item"; added.EstimatedPriceText = "7 usd";
                    await vm.SavePrModalCommand.ExecuteAsync(null); await Settled(vm);
                    var quote2 = (await _repo.GetByIdsAsync(new[] { pr.Id })).Single().Rfqs.Single().Items.FirstOrDefault(i => i.ItemName == "probe new item");
                    Check(quote2 is not null && quote2.LastPrice == 7m && quote2.LastPriceCurrency == "USD",
                          $"a line added in the edit reaches the open quote as 7 {quote2?.LastPriceCurrency}");
                }

                // ------------------------------------------------------ 9 price box and failed save
                Section("9. The price box refuses what it cannot read; a refused or failed save leaves the card alone");
                {
                    var box = new PrItem { ItemName = "box" };
                    box.EstimatedPriceText = "86.75 usd";
                    box.EstimatedPriceText = "12 - 15";
                    Check(box.EstimatedUnitPrice == 86.75m && box.EstimatedCurrency == "USD" && box.HasInvalidPriceText, "a range leaves the price as it was and flags the box");
                    box.EstimatedPriceText = "86.75 x 2";
                    Check(box.EstimatedUnitPrice == 86.75m && box.HasInvalidPriceText, "a multiplication is not stored as 86.752");
                    box.EstimatedPriceText = "90";
                    Check(box.EstimatedUnitPrice == 90m && box.EstimatedCurrency == "USD" && !box.HasInvalidPriceText, "then a real price clears the flag and keeps the currency");
                    box.EstimatedPriceText = "1.234,56 eur";
                    Check(box.EstimatedUnitPrice == 1234.56m && box.EstimatedCurrency == "EUR", "a European price is read as 1234.56 EUR");
                    box.EstimatedPriceText = "";
                    Check(box.EstimatedUnitPrice is null && !box.HasInvalidPriceText, "an empty box clears the price");

                    var pr = await NewPr("J01", ProcurementStatus.PrRaised);
                    var original = pr.PrNo;
                    pr.Items.Add(new PrItem { Id = Guid.NewGuid(), PrId = pr.Id, ItemName = "probe line", Quantity = 1, Unit = "pcs", EstimatedUnitPrice = 86.75m, EstimatedCurrency = "USD", SortOrder = 0 });
                    await _repo.SaveAsync(pr);
                    vm.SearchAnyOf(original); await Settled(vm);
                    var card = vm.FilteredPrs.First(p => p.Id == pr.Id);
                    var originalDescription = card.Description;
                    await vm.OpenEditPrModalCommand.ExecuteAsync(card);

                    vm.EditingPrNo = "";
                    Check(vm.FirstEditError() is { Title: "Validation" } && card.PrNo == original, "a blank PR number is refused and the card keeps its number");
                    vm.EditingPrNo = original;
                    vm.EditingPrItems[0].EstimatedPriceText = "12 - 15";
                    Check(vm.FirstEditError() is { Title: "Price not understood" }, "an unreadable price blocks the save");
                    vm.EditingPrItems[0].EstimatedPriceText = "86.75 usd";
                    Check(vm.FirstEditError() is null, "and fixing it unblocks it");

                    // a save that fails: another connection holds the write lock past the busy timeout
                    vm.EditingPrNo = original + "X";
                    vm.EditingPrDescription = "changed description";
                    vm.EditingPrItems[0].EstimatedPriceText = "99 eur";
                    var sw = Stopwatch.StartNew();
                    using (var blocker = db.CreateConnection())
                    {
                        await blocker.OpenAsync().ConfigureAwait(true);
                        using (var begin = blocker.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE;"; await begin.ExecuteNonQueryAsync().ConfigureAwait(true); }
                        await vm.SavePrModalCommand.ExecuteAsync(null);
                        using (var end = blocker.CreateCommand()) { end.CommandText = "ROLLBACK;"; await end.ExecuteNonQueryAsync().ConfigureAwait(true); }
                    }
                    Check(sw.ElapsedMilliseconds > 3000, $"the save really was blocked ({sw.ElapsedMilliseconds} ms)");
                    Check(card.PrNo == original && card.Description == originalDescription, $"after the failed save the card still shows '{card.PrNo}' and its old description");
                    Check(card.Items.Count == 1 && card.Items[0].EstimatedUnitPrice == 86.75m && card.Items[0].EstimatedCurrency == "USD", "and its old line and price");
                    Check(vm.IsEditModalVisible && vm.EditingPrNo == original + "X" && vm.EditingPrDescription == "changed description", "while the box stays open with the typed text");
                    Check((await _repo.GetByIdsAsync(new[] { pr.Id })).Single().PrNo == original, "and nothing reached the database");

                    // try again once the lock is gone
                    await vm.SavePrModalCommand.ExecuteAsync(null); await Settled(vm);
                    var after = (await _repo.GetByIdsAsync(new[] { pr.Id })).Single();
                    Check(after.PrNo == original + "X" && after.Description == "changed description" && after.Items.Single().EstimatedUnitPrice == 99m && after.Items.Single().EstimatedCurrency == "EUR",
                          "saving again works: the new number, description and 99 EUR are stored");
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
