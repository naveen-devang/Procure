using System;
using System.Diagnostics;

namespace Procure.Utilities
{
    /// <summary>
    /// The runnable check behind UpdateStateStore - proves the round trip actually reads back
    /// what was written (the underlying bug this replaces, Preferences on an unpackaged app, would
    /// have failed this too if it had been caught this way originally: Get after Set returned the
    /// default instead of the value just set).
    ///
    /// Run it by launching a Debug build with PROCURE_UPDATE_SELFCHECK=1 set (same flag as
    /// UpdateCheckSchedulerSelfCheck - both are the update-notification feature's checks).
    /// </summary>
    internal static class UpdateStateStoreSelfCheck
    {
        public static void Run()
        {
            try
            {
                // Last-update-check timestamp round trip.
                var probeTime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
                UpdateStateStore.SetLastUpdateCheckUtc(probeTime);
                var readBackTime = UpdateStateStore.GetLastUpdateCheckUtc();
                if (readBackTime != probeTime)
                    throw new InvalidOperationException($"Expected {probeTime:o} back, got {readBackTime:o} - the write isn't surviving the read.");

                // What's-New version marker round trip - this is the exact value that was silently
                // failing to persist via Preferences, causing the dialog to reshow every launch.
                var probeVersion = "9.9.9-selfcheck";
                UpdateStateStore.SetLastWhatsNewVersionShown(probeVersion);
                var readBackVersion = UpdateStateStore.GetLastWhatsNewVersionShown();
                if (readBackVersion != probeVersion)
                    throw new InvalidOperationException($"Expected \"{probeVersion}\" back, got \"{readBackVersion}\" - the write isn't surviving the read.");

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
            Debug.WriteLine("UpdateStateStoreSelfCheck: " + result);
        }
    }
}
