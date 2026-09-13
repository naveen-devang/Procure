using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.Data
{
    /// <summary>
    /// Times the Tasks page model's real work - the full load, and a rebuild of every view - against
    /// whatever database it is pointed at, so "tasks are personal, hundreds of rows is fine" is a
    /// measured ceiling rather than an assumption someone has to trust.
    ///
    /// The page model derives everything from one in-memory list: counts, sub-task badges, the list,
    /// the board, the calendar and the finished view, and editing works on those same instances. Paging
    /// it is a rewrite of that data flow. This decides whether the rewrite is worth its risk.
    ///
    /// Opt in with PROCURE_TASK_SCALE=1. Point it at a database seeded with many tasks.
    /// </summary>
    public static class TaskScaleProbe
    {
        public static async Task RunAsync(IServiceProvider services)
        {
            var log = new StringBuilder();
            try
            {
                var vm = services.GetRequiredService<TodoPageModel>();
                var sw = Stopwatch.StartNew();

                await vm.LoadAsync(force: true).ConfigureAwait(true);
                var load = sw.Elapsed.TotalMilliseconds;

                log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  task scale probe");
                log.AppendLine($"tasks loaded: {vm.OpenCount} open (top-level)");
                log.AppendLine();
                log.AppendLine($"full load + link labels + first rebuild   {load,8:F1} ms");

                // Each view switch is a full Rebuild over every task. Run each a few times and keep the
                // slowest, since the first pass also pays for JIT.
                foreach (var view in new[] { "List", "Board", "Calendar", "Finished" })
                {
                    var worst = 0.0;
                    for (var i = 0; i < 3; i++)
                    {
                        sw.Restart();
                        vm.CurrentView = view;
                        if (view != "List") vm.CurrentView = "List";
                        vm.CurrentView = view;
                        worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                    }
                    log.AppendLine($"switch to {view,-9} (rebuild, worst of 3)      {worst,8:F1} ms");
                }

                // The filter box rebuilds on every debounced keystroke.
                sw.Restart();
                vm.CurrentView = "List";
                vm.FilterText = "vendor";
                await Task.Delay(400).ConfigureAwait(true);   // past the 250 ms debounce
                log.AppendLine($"filter keystroke (incl. 250 ms debounce)  {sw.Elapsed.TotalMilliseconds,8:F1} ms");

                CrashLog.Write("TASK SCALE PROBE" + Environment.NewLine + log);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(DatabaseConstants.DatabaseDirectory, "task-scale-probe.log"), log.ToString());
            }
            catch (Exception ex)
            {
                CrashLog.Write("TASK SCALE PROBE THREW" + Environment.NewLine + log, ex);
            }
        }
    }
}
