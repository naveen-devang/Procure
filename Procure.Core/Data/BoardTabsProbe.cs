using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.Data
{
    /// <summary>
    /// Drives the real board page model and repository through the Active / Completed / On hold tabs
    /// against whatever database it is pointed at, and reports pass/fail plus what each step cost.
    /// It moves a handful of requisitions between tabs and puts them back, and creates three marked
    /// test requisitions that it deletes at the end - run it on a COPY of the 20,000-PR database.
    ///
    ///     PROCURE_BOARDTABS_PROBE=1  PROCURE_DB_DIR=C:\...\copy-of-procure-20k  Procure.exe
    ///
    /// Result: boardtabs-probe.log next to the database, and the crash log.
    /// </summary>
    internal static class BoardTabsProbe
    {
        private static readonly StringBuilder Log = new();
        private static int _checks, _fails;

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok) _fails++;
            Log.AppendLine((ok ? "  ok    " : "  FAIL  ") + what);
        }

        private static void Note(string text) => Log.AppendLine("        " + text);
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

        /// <summary>Waits for every reload the board has queued to finish and the stale dimming to clear.</summary>
        private static async Task<long> Settled(PrListPageModel vm)
        {
            var sw = Stopwatch.StartNew();
            await Task.Delay(40).ConfigureAwait(true);
            await Until(() => vm.IsReloadIdleForTest && !vm.IsSearchPending && !vm.IsBusy).ConfigureAwait(true);
            return sw.ElapsedMilliseconds;
        }

        private static long HeapSettled()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            return GC.GetTotalMemory(true);
        }

        private static BoardTab TabOf(PurchaseRequisition p) => BoardTabs.Of(p.Status);

        private static PrQuery Q(BoardTab? tab, string? search = null, int take = 1, int skip = 0, string? status = null) =>
            new(search, status, false, false, false, 7, 3, skip, take, false, tab);

        public static async Task RunAsync(IServiceProvider services)
        {
            var created = new List<Guid>();
            var moved = new Dictionary<Guid, string>();   // id -> status to restore
            IPurchaseRequisitionRepository? repo = null;
            try
            {
                var vm = services.GetRequiredService<PrListPageModel>();
                repo = services.GetRequiredService<IPurchaseRequisitionRepository>();
                var db = services.GetRequiredService<SqliteDatabase>();
                Log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  board tabs probe");

                // ---------------------------------------------------------------- 1 partition
                Section("1. The three tabs split the table with no gaps and no overlap");
                int tableTotal;
                using (var conn = db.CreateConnection())
                {
                    await conn.OpenAsync().ConfigureAwait(true);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM PurchaseRequisition";
                    tableTotal = Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(true));
                }
                var sw = Stopwatch.StartNew();
                var tActive = (await repo.GetPageAsync(Q(BoardTab.Active))).TotalCount;
                var tDone = (await repo.GetPageAsync(Q(BoardTab.Completed))).TotalCount;
                var tHold = (await repo.GetPageAsync(Q(BoardTab.OnHold))).TotalCount;
                Note($"table {tableTotal:N0} rows: Active {tActive:N0}, Completed {tDone:N0}, On hold {tHold:N0}  (3 count+page queries {sw.ElapsedMilliseconds} ms)");
                Check(tActive + tDone + tHold == tableTotal, "Active + Completed + On hold equals the whole table");
                Check(tActive > 0 && tDone > 0, "20k data has both active and completed requisitions");

                foreach (var tab in new[] { BoardTab.Active, BoardTab.Completed, BoardTab.OnHold })
                {
                    var page = await repo.GetPageAsync(Q(tab, take: 300));
                    Check(page.Rows.All(p => TabOf(p) == tab), $"every one of the first {page.Rows.Count} {tab} rows has a status that belongs to {tab}");
                    Check(page.Rows.Select(p => p.Id).Distinct().Count() == page.Rows.Count, $"{tab}: no duplicate rows in a page");
                }

                // ---------------------------------------------------------------- 2 odd statuses
                Section("2. Unknown, lower-case and merged statuses still land in exactly one tab");
                var run = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
                var odd = new (string Tag, string Status, BoardTab Expect)[]
                {
                    ("W", "Something Else", BoardTab.Active),
                    ("C", "closed", BoardTab.Completed),
                    ("D", "DELIVERED", BoardTab.Completed),
                    ("H", "on hold", BoardTab.OnHold),
                };
                foreach (var (tag, status, expect) in odd)
                {
                    var pr = new PurchaseRequisition
                    {
                        Id = Guid.NewGuid(), PrNo = $"TP{run}{tag}", Description = "tabs probe " + tag, Requestor = "probe",
                        Plant = ProcurementPlant.RW01, PrType = ProcurementPrType.StoresAndSpares, Status = status,
                    };
                    created.Add(pr.Id);
                    await repo.SaveAsync(pr);
                    var inTabs = new List<BoardTab>();
                    foreach (var tab in new[] { BoardTab.Active, BoardTab.Completed, BoardTab.OnHold })
                        if ((await repo.GetPageAsync(Q(tab, search: pr.PrNo))).Rows.Any(r => r.Id == pr.Id)) inTabs.Add(tab);
                    Check(inTabs.Count == 1 && inTabs[0] == expect, $"status '{status}' appears only in {expect} (found in: {string.Join(", ", inTabs)})");
                }
                var mergedProbe = new PurchaseRequisition
                {
                    Id = Guid.NewGuid(), PrNo = $"TP{run}M", Description = "tabs probe merged", Requestor = "probe",
                    Plant = ProcurementPlant.RW01, PrType = ProcurementPrType.StoresAndSpares, Status = ProcurementStatus.Merged,
                };
                created.Add(mergedProbe.Id);
                await repo.SaveAsync(mergedProbe);
                Check((await repo.GetPageAsync(Q(BoardTab.OnHold, search: mergedProbe.PrNo))).Rows.Any(r => r.Id == mergedProbe.Id), "a Merged requisition is in the On hold tab");
                Check(!(await repo.GetPageAsync(Q(BoardTab.Active, search: mergedProbe.PrNo))).Rows.Any(), "and not in Active");
                Check((await repo.GetPageAsync(Q(null, search: mergedProbe.PrNo))).Rows.Any(r => r.Id == mergedProbe.Id), "searching all tabs finds it too");

                // ---------------------------------------------------------------- 3 lazy open
                Section("3. Opening the board reads Active only");
                var heap0 = HeapSettled();
                vm.BoardAppearing();
                await Until(() => vm.FilteredPrs.Count > 0).ConfigureAwait(true);
                await Settled(vm);
                var openLoaded = vm.LoadedPrs.Count;
                Note($"{vm.FilteredPrs.Count} rows shown, {openLoaded} held in memory, total {vm.TotalFilteredCount:N0}");
                Check(vm.SelectedTab == BoardTab.Active, "the board opens on the Active tab");
                Check(vm.FilteredPrs.All(p => TabOf(p) == BoardTab.Active), "every row on screen is active");
                Check(vm.LoadedPrs.All(p => TabOf(p) == BoardTab.Active), "no Completed or On hold row is in memory");
                Check(openLoaded <= 120, $"only the first page(s) are loaded ({openLoaded}), not the table");
                var activeNow = (await repo.GetPageAsync(Q(BoardTab.Active))).TotalCount;
                Check(vm.TotalFilteredCount == activeNow, $"the Active count {vm.TotalFilteredCount:N0} matches a direct query ({activeNow:N0})");
                Check(vm.StatusFilterOptions.SequenceEqual(new[] { "All" }.Concat(BoardTabs.StatusesOf(BoardTab.Active))), "the status dropdown offers only Active statuses");

                // ---------------------------------------------------------------- 4 switching
                Section("4. Switching tabs");
                var times = new List<long>();
                foreach (var tab in new[] { BoardTab.Completed, BoardTab.OnHold, BoardTab.Active, BoardTab.Completed, BoardTab.Active })
                {
                    var t0 = Stopwatch.StartNew();
                    vm.SelectTab(tab);
                    await Settled(vm);
                    times.Add(t0.ElapsedMilliseconds);
                    Check(vm.SelectedTab == tab && vm.FilteredPrs.Count > 0 && vm.FilteredPrs.All(p => TabOf(p) == tab),
                          $"{tab}: {vm.FilteredPrs.Count} rows, all {tab}, total {vm.TotalFilteredCount:N0} ({t0.ElapsedMilliseconds} ms)");
                    Check(vm.LoadedPrs.All(p => TabOf(p) == tab), $"{tab}: nothing from another tab is held in memory ({vm.LoadedPrs.Count} rows)");
                }
                Note($"tab switch ms: {string.Join(", ", times)}  (median {times.OrderBy(x => x).ElementAt(times.Count / 2)} ms)");

                // Cost of a switch, repeated after the warm-up above: the board path and the bare query.
                var perTab = new Dictionary<BoardTab, List<long>> { [BoardTab.Active] = new(), [BoardTab.Completed] = new(), [BoardTab.OnHold] = new() };
                var bare = new Dictionary<BoardTab, List<long>> { [BoardTab.Active] = new(), [BoardTab.Completed] = new(), [BoardTab.OnHold] = new() };
                for (var round = 0; round < 8; round++)
                    foreach (var tab in new[] { BoardTab.Completed, BoardTab.OnHold, BoardTab.Active })
                    {
                        var tt = Stopwatch.StartNew();
                        vm.SelectTab(tab);
                        await Settled(vm);
                        perTab[tab].Add(tt.ElapsedMilliseconds - 40);   // minus Settled's fixed 40 ms head start
                        var qt = Stopwatch.StartNew();
                        await repo.GetPageAsync(Q(tab, take: 50));
                        bare[tab].Add(qt.ElapsedMilliseconds);
                    }
                // The same thing the board has always done for a filter change, as the yardstick.
                var filterMs = new List<long>();
                for (var i = 0; i < 8; i++)
                {
                    var ft = Stopwatch.StartNew();
                    vm.SelectedStatusFilter = i % 2 == 0 ? ProcurementStatus.PoRaised : "All";
                    await Settled(vm);
                    filterMs.Add(ft.ElapsedMilliseconds - 40);
                }
                vm.SelectedStatusFilter = "All"; await Settled(vm);
                static string Stat(List<long> v) => $"median {v.OrderBy(x => x).ElementAt(v.Count / 2)} ms, max {v.Max()} ms";
                Note($"for comparison, the existing status-dropdown change on the Active tab: {Stat(filterMs)}");
                foreach (var tab in perTab.Keys)
                    Note($"{tab,-9} board switch: {Stat(perTab[tab])}   |   bare page query (50 rows with children): {Stat(bare[tab])}");

                vm.SelectTab(BoardTab.Completed); await Settled(vm);
                Check(vm.StatusFilterOptions.SequenceEqual(new[] { "All" }.Concat(BoardTabs.CompletedStatuses)), "Completed's dropdown offers Delivered and Closed");
                vm.SelectedStatusFilter = ProcurementStatus.Closed; await Settled(vm);
                Note($"Closed filter: {vm.FilteredPrs.Count} rows; statuses {string.Join(", ", vm.FilteredPrs.Select(p => p.Status).Distinct())}; total {vm.TotalFilteredCount}");
                Check(vm.FilteredPrs.Count > 0 && vm.FilteredPrs.All(p => string.Equals(p.Status, ProcurementStatus.Closed, StringComparison.OrdinalIgnoreCase)), "the dropdown narrows inside the tab (Closed only, any case)");
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                Check(vm.SelectedStatusFilter == "All", "changing tab puts the dropdown back to All");

                // ---------------------------------------------------------------- 5 scroll
                Section("5. Scrolling a tab pages in order with no duplicates");
                foreach (var tab in new[] { BoardTab.Active, BoardTab.Completed })
                {
                    vm.SelectTab(tab); await Settled(vm);
                    var guard = 0;
                    while (vm.FilteredPrs.Count < 400 && vm.LoadMoreCommand.CanExecute(null) && guard++ < 40)
                    {
                        var before = vm.FilteredPrs.Count;
                        await vm.LoadMoreCommand.ExecuteAsync(null).ConfigureAwait(true);
                        if (vm.FilteredPrs.Count == before) break;
                    }
                    var ids = vm.FilteredPrs.Select(p => p.Id).ToList();
                    Check(ids.Count >= 200, $"{tab}: scrolled to {ids.Count} rows");
                    Check(ids.Distinct().Count() == ids.Count, $"{tab}: no row appears twice");
                    Check(vm.FilteredPrs.All(p => TabOf(p) == tab), $"{tab}: every scrolled row belongs to the tab");
                    var sorted = vm.FilteredPrs.Zip(vm.FilteredPrs.Skip(1), (a, b) => a.CreatedAt >= b.CreatedAt).All(x => x);
                    Check(sorted, $"{tab}: newest first all the way down");
                }

                // ---------------------------------------------------------------- 6 selection & moves
                Section("6. Mark completed / Hold / Reopen / Resume");
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                var pick = vm.FilteredPrs.Where(p => p.Status != ProcurementStatus.Merged).Take(3).ToList();
                var pickIds = pick.Select(p => p.Id).ToList();
                var pickNos = pick.Select(p => p.PrNo).ToList();
                foreach (var p in pick) moved[p.Id] = p.Status;
                foreach (var p in pick) vm.SetSelected(p, true);
                Check(vm.SelectedPrsCount == 3 && vm.CanMarkCompleted && vm.CanHoldCancel && !vm.CanReopen, "3 ticked in Active: Mark completed and Hold/cancel offered, Reopen not");

                vm.SelectTab(BoardTab.Completed); await Settled(vm);
                Check(vm.SelectedPrsCount == 0 && !vm.IsBatchSelectionActive, "switching tab clears the selection");
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                pick = vm.FilteredPrs.Where(p => pickIds.Contains(p.Id)).ToList();
                foreach (var p in pick) vm.SetSelected(p, true);

                var activeBefore = vm.TotalFilteredCount;
                var doneBefore = (await repo.GetPageAsync(Q(BoardTab.Completed))).TotalCount;
                var t1 = Stopwatch.StartNew();
                await vm.MarkSelectedCompletedCommand.ExecuteAsync(null).ConfigureAwait(true);
                await Settled(vm);
                Note($"Mark completed on 3 requisitions: {t1.ElapsedMilliseconds} ms to the board settling");
                Check(!vm.FilteredPrs.Any(p => pickIds.Contains(p.Id)), "the three left the Active tab");
                Check(vm.TotalFilteredCount == activeBefore - 3, $"Active count {activeBefore:N0} -> {vm.TotalFilteredCount:N0}");
                Check(vm.SelectedPrsCount == 0, "the selection is cleared");
                var fresh = await repo.GetByIdsAsync(pickIds);
                Check(fresh.Count == 3 && fresh.All(p => p.Status == ProcurementStatus.Closed), "all three are Closed in the database");
                Check((await repo.GetPageAsync(Q(BoardTab.Completed))).TotalCount == doneBefore + 3, "Completed gained exactly 3");
                Check(vm.FilteredPrs.Select(p => p.Id).Distinct().Count() == vm.FilteredPrs.Count, "no duplicates on the Active board");

                vm.SelectTab(BoardTab.Completed); await Settled(vm);
                foreach (var no in pickNos)
                {
                    vm.SearchText = no; await Settled(vm);
                    Check(vm.FilteredPrs.Count(p => p.PrNo == no) == 1, $"{no} is in Completed exactly once (search inside the tab)");
                }
                vm.SearchText = string.Empty; await Settled(vm);

                // reopen from Completed
                vm.SearchText = pickNos[0]; await Settled(vm);
                var row = vm.FilteredPrs.First(p => p.PrNo == pickNos[0]);
                vm.SetSelected(row, true);
                Check(vm.CanReopen && vm.ReopenButtonText == "Reopen" && !vm.CanMarkCompleted, "a Completed row offers Reopen, not Mark completed");
                await vm.ReopenSelectedCommand.ExecuteAsync(null).ConfigureAwait(true);
                await Settled(vm);
                Check(!vm.FilteredPrs.Any(p => p.PrNo == pickNos[0]), "the reopened row left Completed");
                vm.SearchText = string.Empty; await Settled(vm);
                var reopened = (await repo.GetByIdsAsync(new[] { pickIds[0] })).Single();
                Check(BoardTabs.Of(reopened.Status) == BoardTab.Active, $"and is active again as '{reopened.Status}'");
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                vm.SearchText = pickNos[0]; await Settled(vm);
                Check(vm.FilteredPrs.Count(p => p.PrNo == pickNos[0]) == 1, "it is in Active exactly once (no duplicate after the round trip)");
                vm.SearchText = string.Empty; await Settled(vm);

                // hold, then resume
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                var holdRows = vm.FilteredPrs.Where(p => p.Status != ProcurementStatus.Merged).Take(2).ToList();
                foreach (var p in holdRows) { moved.TryAdd(p.Id, p.Status); vm.SetSelected(p, true); }
                var holdIds = holdRows.Select(p => p.Id).ToList();
                var holdBefore = (await repo.GetPageAsync(Q(BoardTab.OnHold))).TotalCount;
                await vm.HoldSelectedCommand.ExecuteAsync(null).ConfigureAwait(true);
                await Settled(vm);
                Check(!vm.FilteredPrs.Any(p => holdIds.Contains(p.Id)), "two put on hold left Active");
                Check((await repo.GetPageAsync(Q(BoardTab.OnHold))).TotalCount == holdBefore + 2, "On hold gained exactly 2");
                vm.SelectTab(BoardTab.OnHold); await Settled(vm);
                vm.SearchText = holdRows[0].PrNo; await Settled(vm);
                var heldRow = vm.FilteredPrs.FirstOrDefault(p => p.PrNo == holdRows[0].PrNo);
                Check(heldRow != null && heldRow.Status == ProcurementStatus.OnHold, "it shows in On hold with status On Hold");
                if (heldRow != null) vm.SetSelected(heldRow, true);
                Check(vm.CanReopen && vm.ReopenButtonText == "Resume" && !vm.CanHoldCancel, "an On hold row offers Resume");
                await vm.ReopenSelectedCommand.ExecuteAsync(null).ConfigureAwait(true);
                await Settled(vm);
                Check(!vm.FilteredPrs.Any(p => p.PrNo == holdRows[0].PrNo), "resumed row left On hold");
                vm.SearchText = string.Empty; await Settled(vm);

                // merged source is never moved
                vm.SelectTab(BoardTab.OnHold); await Settled(vm);
                vm.SearchText = mergedProbe.PrNo; await Settled(vm);
                var mergedRow = vm.FilteredPrs.FirstOrDefault(p => p.Id == mergedProbe.Id);
                Check(mergedRow != null, "the Merged test row shows in On hold");
                if (mergedRow != null)
                {
                    vm.SetSelected(mergedRow, true);
                    Check(!vm.CanReopen && !vm.CanMarkCompleted && !vm.CanHoldCancel, "a Merged row offers no move buttons");
                    await vm.ReopenSelectedCommand.ExecuteAsync(null).ConfigureAwait(true);
                    await Settled(vm);
                    var still = (await repo.GetByIdsAsync(new[] { mergedProbe.Id })).Single();
                    Check(still.Status == ProcurementStatus.Merged, "forcing Resume leaves it Merged");
                    vm.ClearSelectionCommand.Execute(null);
                }
                vm.SearchText = string.Empty; await Settled(vm);

                // ---------------------------------------------------------------- 7 search scope
                Section("7. Search is per tab, with a way to look everywhere");
                vm.SelectTab(BoardTab.Completed); await Settled(vm);
                var doneNo = vm.FilteredPrs.First().PrNo;
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                vm.SearchText = doneNo; await Settled(vm);
                Check(vm.FilteredPrs.Count == 0 && vm.CanSearchAllTabs, $"'{doneNo}' (a Completed PR) finds nothing in Active and offers 'Search all tabs'");
                vm.SearchEveryTabCommand.Execute(null); await Settled(vm);
                Check(vm.SearchAllTabs && vm.FilteredPrs.Any(p => p.PrNo == doneNo), "Search all tabs finds it");
                Check(vm.StatusFilterOptions.Count == 1 + ProcurementStatus.AllStatuses.Length, "the dropdown lists every status while searching everywhere");
                vm.SearchText = string.Empty; await Settled(vm);
                Check(!vm.SearchAllTabs && vm.FilteredPrs.All(p => TabOf(p) == BoardTab.Active), "clearing the search returns to the Active tab");
                vm.SearchAnyOf(doneNo); await Settled(vm);
                Check(vm.SearchAllTabs && vm.FilteredPrs.Any(p => p.PrNo == doneNo), "a task/note link (SearchAnyOf) searches every tab");
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                Check(!vm.SearchAllTabs, "picking a tab returns to tab scope");
                vm.SearchText = string.Empty; await Settled(vm);
                vm.ResetFiltersCommand.Execute(null); await Settled(vm);

                // ---------------------------------------------------------------- 8 status edits
                Section("8. A status edit moves the row at once; other paths are caught by the backstop");
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                var editRow = vm.FilteredPrs.First(p => p.Status != ProcurementStatus.Merged);
                moved.TryAdd(editRow.Id, editRow.Status);
                await vm.UpdatePrStatusDirectAsync(editRow, ProcurementStatus.Delivered).ConfigureAwait(true);
                await Settled(vm);
                Check(!vm.FilteredPrs.Contains(editRow), "setting Delivered through the status pill takes the row out of Active");

                var backRow = vm.FilteredPrs.Skip(1).First(p => p.Status != ProcurementStatus.Merged);
                moved.TryAdd(backRow.Id, backRow.Status);
                backRow.Status = ProcurementStatus.Closed;                  // a path that does not reload the board
                await repo.SavePrFieldsAsync(backRow).ConfigureAwait(true);
                var gone = await Until(() => !vm.FilteredPrs.Contains(backRow), 5000).ConfigureAwait(true);
                Check(gone, "a status changed elsewhere (saved, no reload call) leaves the tab within ~1.2 s");

                // ---------------------------------------------------------------- 9 stress / memory
                Section("9. Rapid switching, and memory after many switches");
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                var heapA = HeapSettled();
                var order = new[] { BoardTab.Completed, BoardTab.OnHold, BoardTab.Active };
                for (var i = 0; i < 30; i++) vm.SelectTab(order[i % 3]);   // no waiting: a user clicking fast
                vm.SelectTab(BoardTab.Completed);
                await Settled(vm);
                Check(vm.SelectedTab == BoardTab.Completed && vm.FilteredPrs.Count > 0 && vm.FilteredPrs.All(p => TabOf(p) == BoardTab.Completed),
                      $"after 31 clicks without waiting the board shows exactly the last tab ({vm.FilteredPrs.Count} rows)");
                Check(vm.FilteredPrs.Select(p => p.Id).Distinct().Count() == vm.FilteredPrs.Count, "and no duplicate rows");
                var passes0 = vm.ReloadPassesForTest;
                for (var i = 0; i < 60; i++)
                {
                    vm.SelectTab(order[i % 3]);
                    await Settled(vm);
                }
                vm.SelectTab(BoardTab.Active); await Settled(vm);
                await Task.Delay(2500).ConfigureAwait(true);
                var heapB = HeapSettled();
                Note($"managed heap after the first Active load {heapA / 1048576.0:F1} MB, after 60 more switches {heapB / 1048576.0:F1} MB; board holds {vm.LoadedPrs.Count} rows");
                Check(heapB - heapA < 25L * 1048576, $"heap grew {(heapB - heapA) / 1048576.0:F1} MB over 60 switches (limit 25 MB)");
                Check(vm.LoadedPrs.Count <= 120, $"only {vm.LoadedPrs.Count} rows are held after the stress run");
                Check(vm.LoadedPrs.All(p => TabOf(p) == BoardTab.Active), "and all are active");

                // ---------------------------------------------------------------- 10 leave, return, refresh
                Section("10. Leaving the board, coming back, and Refresh keep the open tab");
                vm.SelectTab(BoardTab.Completed); await Settled(vm);
                var guard2 = 0;
                while (vm.FilteredPrs.Count < 600 && vm.LoadMoreCommand.CanExecute(null) && guard2++ < 60)
                {
                    var before = vm.FilteredPrs.Count;
                    await vm.LoadMoreCommand.ExecuteAsync(null).ConfigureAwait(true);
                    if (vm.FilteredPrs.Count == before) break;
                }
                Note($"scrolled Completed to {vm.FilteredPrs.Count} rows");
                var released = vm.BoardDisappearing();
                Check(released && vm.LoadedPrs.Count == 0 && vm.FilteredPrs.Count == 0, "leaving the board with 600 rows loaded releases them all");
                vm.BoardAppearing();
                await Until(() => vm.FilteredPrs.Count > 0).ConfigureAwait(true);
                await Settled(vm);
                Check(vm.SelectedTab == BoardTab.Completed && vm.FilteredPrs.Count > 0 && vm.FilteredPrs.All(p => TabOf(p) == BoardTab.Completed),
                      $"coming back reloads the Completed tab it was on ({vm.FilteredPrs.Count} rows)");
                vm.SelectTab(BoardTab.OnHold); await Settled(vm);
                await vm.RefreshBoardCommand.ExecuteAsync(null).ConfigureAwait(true);
                await Until(() => vm.FilteredPrs.Count > 0).ConfigureAwait(true);
                await Settled(vm);
                Check(vm.SelectedTab == BoardTab.OnHold && vm.FilteredPrs.Count > 0 && vm.FilteredPrs.All(p => TabOf(p) == BoardTab.OnHold), "Refresh stays on the On hold tab");
                vm.SearchText = "zzzz-no-such-pr"; await Settled(vm);
                Check(vm.FilteredPrs.Count == 0 && vm.TotalFilteredCount == 0 && vm.CanSearchAllTabs, "a search with no match empties the tab and offers 'Search all tabs'");
                vm.SearchText = string.Empty; await Settled(vm);
                vm.SelectTab(BoardTab.Active); await Settled(vm);
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
                    if (repo is not null)
                    {
                        foreach (var id in created) { try { await repo.DeleteAsync(id).ConfigureAwait(true); } catch { /* best effort */ } }
                        foreach (var (id, status) in moved)
                        {
                            var pr = (await repo.GetByIdsAsync(new[] { id }).ConfigureAwait(true)).FirstOrDefault();
                            if (pr is null || pr.Status == status) continue;
                            pr.Status = status;
                            await repo.SavePrFieldsAsync(pr).ConfigureAwait(true);
                        }
                    }
                }
                catch (Exception ex) { Log.AppendLine("  cleanup threw " + ex.Message); }

                var summary = _fails == 0 ? $"PASS  {_checks} checks" : $"FAIL  {_fails} of {_checks} checks failed";
                Log.Insert(0, "BOARD TABS PROBE " + summary + Environment.NewLine);
                CrashLog.Write("BOARD TABS PROBE " + summary + Environment.NewLine + Log);
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(DatabaseConstants.DatabaseDirectory, "boardtabs-probe.log"), Log.ToString()); }
                catch { /* the crash log already has it */ }
            }
        }
    }
}
