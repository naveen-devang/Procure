using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Procure.Data;

namespace Procure.Utilities
{
    // ponytail: TEMPORARY diagnostic scaffolding. Delete this file, the `#region ponytail-temp`
    // block in PurchaseRequisitionRepository.cs, and every `probe.` line (grep "ponytail-temp")
    // once the numbers are captured.
    internal sealed class TimingProbe
    {
        private static readonly ConcurrentDictionary<string, int> Counts = new();

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly List<(string Label, long Ticks)> _marks = new(16);
        private readonly string _name;
        private readonly int _call;

        private TimingProbe(string name)
        {
            _name = name;
            _call = Counts.AddOrUpdate(name, 1, static (_, n) => n + 1);
        }

        public static TimingProbe Start(string name) => new(name);

        // Record only — no formatting, no I/O — so the probe does not distort what it measures.
        public void Mark(string label) => _marks.Add((label, _sw.ElapsedTicks));

        public void Flush()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"=== {_name} call #{_call} ({(_call == 1 ? "COLD" : "warm")}) {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
                long prev = 0;
                foreach (var (label, ticks) in _marks)
                {
                    sb.AppendLine($"  {label,-24} step {(ticks - prev) * 1000.0 / Stopwatch.Frequency,9:F1} ms   total {ticks * 1000.0 / Stopwatch.Frequency,9:F1} ms");
                    prev = ticks;
                }

                File.AppendAllText(Path.Combine(DatabaseConstants.DatabaseDirectory, "getall-timing.log"), sb.ToString());
            }
            catch
            {
                // Diagnostics must never break the load path.
            }
        }
    }
}
