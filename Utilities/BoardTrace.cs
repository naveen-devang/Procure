using System;
using System.Diagnostics;
using System.IO;

namespace Procure.Utilities
{
    /// <summary>Millisecond-stamped marks for the PR Board opening flow, appended to the file named
    /// by PROCURE_TRACE. Companion env var PROCURE_TRACE_NAV=&lt;ms&gt; makes AppShell navigate to the
    /// board at that offset, so "user clicks PR Board N ms after launch" reproduces identically
    /// across runs. Diagnostics-only, like BoardBench: zero cost unless PROCURE_TRACE is set.</summary>
    public static class BoardTrace
    {
        private static readonly string? TracePath = Environment.GetEnvironmentVariable("PROCURE_TRACE");
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static bool IsEnabled => TracePath is not null;

        public static void Mark(string label)
        {
            if (TracePath is null) return;
            try
            {
                File.AppendAllText(TracePath, $"{Clock.ElapsedMilliseconds,6} ms  {label}{Environment.NewLine}");
            }
            catch
            {
                // Tracing must never take the app down.
            }
        }

        /// <summary>A 15ms dispatcher heartbeat: any gap over 100ms means the UI thread was blocked
        /// for that long (layout, realization, inflation - everything), logged as ui-blocked with the
        /// block's end timestamp. This is the ground truth for freeze duration.</summary>
        public static void StartPulse(Microsoft.Maui.Dispatching.IDispatcher dispatcher)
        {
            if (TracePath is null) return;
            var last = Clock.ElapsedMilliseconds;
            void Tick()
            {
                var now = Clock.ElapsedMilliseconds;
                if (now - last > 100) Mark($"ui-blocked {now - last} ms (block ended here)");
                last = now;
                dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(15), Tick);
            }
            dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(15), Tick);
        }
    }
}
