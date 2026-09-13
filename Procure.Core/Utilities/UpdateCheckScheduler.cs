using System;

namespace Procure.Utilities
{
    // Pure, testable gate for "is it time to check for updates again" - kept separate from
    // App.xaml.cs's Preferences/IUpdateService plumbing so UpdateCheckSchedulerSelfCheck can
    // exercise it directly with fabricated timestamps, no running app or network needed.
    public static class UpdateCheckScheduler
    {
        public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(24);

        public static bool ShouldCheckNow(DateTime lastCheckUtc, DateTime nowUtc)
            => nowUtc - lastCheckUtc >= MinimumInterval;
    }
}
