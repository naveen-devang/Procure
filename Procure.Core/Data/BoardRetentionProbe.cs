using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Procure.Models;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.Data
{
    /// <summary>
    /// Answers two questions about the board that Task Manager cannot: does scrolling retain what it
    /// loads, and does Refresh let go of it.
    ///
    /// Working set says neither. A .NET process holds on to memory the collector has already freed,
    /// so a number that only goes up is equally consistent with "nothing is ever released" and "the
    /// collector has had no reason to run". The difference matters completely - one is a leak, the
    /// other is the runtime behaving normally - and the only way to tell them apart is to ask whether
    /// the objects are still REACHABLE.
    ///
    /// So this holds weak references to rows the board loaded, does the thing under suspicion, forces
    /// a full collection, and reports how many of those rows are still alive. A row that survives a
    /// forced collection is genuinely still referenced by something; a row that dies was never a leak.
    /// It also reports the managed heap at each step, after collection, which is the only figure that
    /// can be honestly described as "what the board is costing".
    ///
    /// Opt in with PROCURE_BOARD_MEMORY=1. Debug only. It drives the live board, so run it instead of
    /// using the app, not alongside it.
    /// </summary>
    public static class BoardRetentionProbe
    {
        private const int Step = 100;      // rows between samples
        private const int Target = 500;    // rows to scroll to

        public static async Task RunAsync(IServiceProvider services)
        {
            var log = new StringBuilder();
            try
            {
                var vm = services.GetService<PrListPageModel>();
                if (vm is null) { CrashLog.Write("BOARD MEMORY PROBE: no page model in the container"); return; }

                log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  board retention probe");
                log.AppendLine();
                // Managed heap AND working set, because they answer different questions. The heap is
                // the board's data; the working set also carries the realized UI, SQLite's page cache
                // and everything the runtime has not handed back to Windows. A complaint about "RAM"
                // is always about the second one, and it is usually mostly the third thing.
                log.AppendLine("step                          rows   heap MB  heap/100   WS MB   WS/100");

                var baseline = Settle();
                var wsBaseline = WorkingSet();
                log.AppendLine($"{"baseline (empty board)",-28} {0,5} {Mb(baseline),9:F1} {"",9} {Mb(wsBaseline),7:F0}");

                // ---- scrolling ------------------------------------------------------------------
                vm.BoardAppearing();
                await WaitForRowsAsync(vm, 1).ConfigureAwait(true);

                var afterFirst = Settle();
                var wsFirst = WorkingSet();
                var previous = afterFirst;
                log.AppendLine($"{"first page",-28} {vm.FilteredPrs.Count,5} {Mb(afterFirst),9:F1} {"",9} {Mb(wsFirst),7:F0}");

                var next = Step;
                while (vm.FilteredPrs.Count < Target)
                {
                    var before = vm.FilteredPrs.Count;
                    if (vm.LoadMoreCommand.CanExecute(null)) await vm.LoadMoreCommand.ExecuteAsync(null).ConfigureAwait(true);
                    if (vm.FilteredPrs.Count == before) break;      // nothing more to load

                    if (vm.FilteredPrs.Count >= next)
                    {
                        var here = Settle();
                        var ws = WorkingSet();
                        var rows = vm.FilteredPrs.Count;
                        var since = Math.Max(rows - 16, 1);
                        log.AppendLine($"{"scrolled",-28} {rows,5} {Mb(here),9:F1} {Mb(here - afterFirst) / since * 100,9:F1} {Mb(ws),7:F0} {Mb(ws - wsFirst) / since * 100,8:F1}");
                        previous = here;
                        next = rows + Step;
                    }
                }

                var loaded = vm.FilteredPrs.Count;
                var afterScroll = Settle();

                // ---- the question: does Refresh let go? -----------------------------------------
                // Weak references to every row currently on the board. After Refresh and a forced
                // collection, any that are still alive are still referenced by something.
                var watched = vm.FilteredPrs.Select(pr => new WeakReference<PurchaseRequisition>(pr)).ToList();
                var watchedIds = vm.FilteredPrs.Select(pr => pr.Id).ToHashSet();
                var watchedOrder = vm.FilteredPrs.Select(pr => pr.Id).ToList();

                var wsBeforeRefresh = WorkingSet();
                await vm.RefreshBoardCommand.ExecuteAsync(null).ConfigureAwait(true);
                await WaitForRowsAsync(vm, 1).ConfigureAwait(true);

                // What the user watching Task Manager sees: working set a few seconds after pressing
                // Refresh, with nothing forced. This is the number that used to stay flat.
                await Task.Delay(3000).ConfigureAwait(true);
                var wsUnforced = WorkingSet();

                var afterRefresh = Settle();
                var wsRefresh = WorkingSet();
                var reloadedIds = vm.FilteredPrs.Select(pr => pr.Id).ToHashSet();

                // A row the board legitimately still shows is not a leak - the first page comes back.
                // Counted among the SURVIVORS, not among the ids: a reload that built fresh instances
                // for the same requisitions leaves nothing alive to explain, and comparing id sets
                // reported that as -66 retained rows.
                var stillShown = 0;
                var survivors = 0;
                var unexplained = new List<string>();
                foreach (var w in watched)
                {
                    if (!w.TryGetTarget(out var alive)) continue;
                    survivors++;
                    if (reloadedIds.Contains(alive.Id)) stillShown++;
                    // Naming them is the point: "4 rows are retained" is a mystery, "the 4 rows the
                    // list was last showing" is a recycled container pool and nothing to fix.
                    if (!reloadedIds.Contains(alive.Id))
                        unexplained.Add($"{alive.PrNo} (index {watchedOrder.IndexOf(alive.Id)} of {loaded})");
                }
                var leaked = survivors - stillShown;

                log.AppendLine();
                log.AppendLine($"{"after Refresh",-28} {vm.FilteredPrs.Count,5} {Mb(afterRefresh),9:F1} {"",9} {Mb(wsRefresh),7:F0}");
                log.AppendLine();
                log.AppendLine($"working set before Refresh:      {Mb(wsBeforeRefresh):F0} MB");
                log.AppendLine($"...3s after Refresh, unforced:   {Mb(wsUnforced):F0} MB   ({Mb(wsUnforced - wsBeforeRefresh):+0;-0;0} MB)");
                log.AppendLine();
                log.AppendLine($"rows loaded before Refresh:      {loaded}");
                log.AppendLine($"still reachable after Refresh:   {survivors}");
                log.AppendLine($"  ...of which the board shows:   {stillShown}   (expected - it reloaded them)");
                log.AppendLine($"  ...unaccounted for:            {leaked}   {(leaked == 0 ? "<- nothing retained" : "<- RETAINED, something still holds these")}");
                foreach (var u in unexplained) log.AppendLine($"      retained: {u}");
                log.AppendLine();
                log.AppendLine($"heap returned to within {Mb(afterRefresh - afterFirst):F1} MB of a freshly loaded board " +
                               $"(was {Mb(afterScroll - afterFirst):F1} MB above it with {loaded} rows loaded)");

                CrashLog.Write("BOARD MEMORY PROBE" + Environment.NewLine + log);
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(DatabaseConstants.DatabaseDirectory, "board-memory-probe.log"), log.ToString()); }
                catch { /* the crash log already has it */ }
            }
            catch (Exception ex)
            {
                CrashLog.Write("BOARD MEMORY PROBE THREW" + Environment.NewLine + log, ex);
            }
        }

        /// <summary>Collects twice with finalizers in between, then reports the managed heap. Twice
        /// because the first pass runs finalizers that can themselves release more.</summary>
        private static long Settle()
        {
            for (var i = 0; i < 2; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

        private static long WorkingSet()
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();
            return p.WorkingSet64;
        }

        /// <summary>The board loads asynchronously off its own commands; wait for rows to appear
        /// rather than guessing at a delay.</summary>
        private static async Task WaitForRowsAsync(PrListPageModel vm, int atLeast)
        {
            for (var i = 0; i < 100 && vm.FilteredPrs.Count < atLeast; i++)
                await Task.Delay(100).ConfigureAwait(true);
        }
    }
}
