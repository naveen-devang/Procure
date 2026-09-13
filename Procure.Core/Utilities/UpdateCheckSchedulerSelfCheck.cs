using System;
using System.Diagnostics;

namespace Procure.Utilities
{
    /// <summary>
    /// The runnable check behind the silent auto-update loop's "at least once a day" gate
    /// (App.xaml.cs's CheckForUpdatesInBackgroundAsync). Pure logic, no app state touched - safe
    /// to run any time.
    ///
    /// Run it by launching a Debug build with PROCURE_UPDATE_SELFCHECK=1 set.
    /// </summary>
    internal static class UpdateCheckSchedulerSelfCheck
    {
        public static void Run()
        {
            try
            {
                var now = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);

                // Never checked before - a huge gap should always say "check now".
                if (!UpdateCheckScheduler.ShouldCheckNow(DateTime.MinValue, now))
                    throw new InvalidOperationException("A never-checked (DateTime.MinValue) last-check time must trigger a check.");

                // Checked 23 hours ago - not yet due.
                if (UpdateCheckScheduler.ShouldCheckNow(now.AddHours(-23), now))
                    throw new InvalidOperationException("23 hours since the last check must not trigger another one yet.");

                // Checked exactly 24 hours ago - due (boundary is inclusive).
                if (!UpdateCheckScheduler.ShouldCheckNow(now.AddHours(-24), now))
                    throw new InvalidOperationException("Exactly 24 hours since the last check must trigger a check.");

                // Checked 25 hours ago - overdue, still due.
                if (!UpdateCheckScheduler.ShouldCheckNow(now.AddHours(-25), now))
                    throw new InvalidOperationException("25 hours since the last check must trigger a check.");

                // Checked 1 minute ago - nowhere close.
                if (UpdateCheckScheduler.ShouldCheckNow(now.AddMinutes(-1), now))
                    throw new InvalidOperationException("1 minute since the last check must not trigger another one.");

                // A clock that moved backwards (DST, manual change) must not wedge the app into
                // never checking again - a negative gap is still "not due", not a crash.
                var future = now.AddHours(1);
                if (UpdateCheckScheduler.ShouldCheckNow(future, now))
                    throw new InvalidOperationException("A last-check time in the future must not trigger a check, and must not throw.");

                Report("PASS");
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message);
                throw;
            }
        }

        private static void Report(string result)
        {
            Debug.WriteLine("UpdateCheckSchedulerSelfCheck: " + result);
        }
    }
}
