#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Procure.Data;

namespace Procure.Utilities
{
    /// <summary>
    /// Measures what it costs the board to grow. The list is a BindableLayout inside a
    /// VerticalStackLayout, which re-measures its children whenever one is added, so the cost of
    /// revealing the next batch rises with how many cards are already realised - revealing n cards is
    /// O(n^2). This reports that curve so the decision to virtualize (or not) rests on numbers.
    ///
    /// Debug only, and only when asked for:
    ///
    ///     PROCURE_BOARDBENCH=1  PROCURE_DB_DIR=C:/temp/procure-20k  Procure.exe
    ///
    /// Open the PR Board and it reveals to the end, writing board-bench.csv next to the database.
    /// Generate a database to run it against with Tools/generate-test-db.py.
    /// </summary>
    internal static class BoardBench
    {
        public const string EnableVariable = "PROCURE_BOARDBENCH";

        public static bool IsEnabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

        /// <param name="cardCount">Cards currently on the board.</param>
        /// <param name="revealMore">Grows the board by one batch.</param>
        /// <param name="settle">Completes once the dispatcher has drained the work that queued.</param>
        public static async Task RunAsync(Func<int> cardCount, Action revealMore, Func<Task> settle,
                                          int maxCards = 2000, int budgetSeconds = 180)
        {
            var log = new StringBuilder("cards_after,batch_ms,working_set_mb" + Environment.NewLine);
            var sw = new Stopwatch();
            var budget = Stopwatch.StartNew();

            // Bounded twice over. The cost of a batch grows with the number of cards already realised,
            // so an unbounded run on a large table takes minutes and tells you nothing the first few
            // hundred cards did not - and nobody scrolls that far anyway.
            while (cardCount() < maxCards && budget.Elapsed.TotalSeconds < budgetSeconds)
            {
                var before = cardCount();
                sw.Restart();
                revealMore();

                // 5ms polling, so a reported time is the real cost plus at most one poll.
                var guard = 0;
                while (cardCount() == before && guard++ < 2000) await Task.Delay(5);
                if (cardCount() == before) break;   // nothing left to reveal

                await settle();
                // Memory is the crash question: unvirtualized this climbed ~2.2 MB per card until
                // the process died. It has to stay flat here however far the run goes.
                var mb = Process.GetCurrentProcess().WorkingSet64 / 1048576;
                log.Append($"{cardCount()},{sw.Elapsed.TotalMilliseconds:F0},{mb}").Append(Environment.NewLine);

                // Written every batch, not at the end: a run that is cut short still leaves its curve.
                Write(log);
            }

            Write(log);
        }

        private static void Write(StringBuilder log)
        {
            try
            {
                File.WriteAllText(Path.Combine(DatabaseConstants.DatabaseDirectory, "board-bench.csv"),
                                  log.ToString());
            }
            catch
            {
                // A measurement must never be the thing that breaks the run.
            }
        }

        /// <summary>Completes after the layout and render work already queued ahead of us.</summary>
        public static Task SettleAsync(IDispatcher dispatcher)
        {
            var done = new TaskCompletionSource();
            dispatcher.Dispatch(() => dispatcher.Dispatch(() => done.SetResult()));
            return done.Task;
        }
    }
}
#endif
