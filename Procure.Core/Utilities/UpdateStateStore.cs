using System;
using System.Globalization;
using System.IO;

namespace Procure.Utilities
{
    // Plain-file persistence for the two small update-related markers App.xaml.cs needs across
    // restarts. Deliberately not Microsoft.Maui.Storage.Preferences: on Windows it backs onto
    // Windows.Storage.ApplicationData.Current.LocalSettings, which needs WinRT app-identity
    // plumbing this unpackaged, Velopack-installed app doesn't have - writes there weren't
    // reliably sticking between launches, which is why the "What's New" dialog kept reshowing
    // every single time instead of once. FileSystem.AppDataDirectory is the same proven-reliable
    // location the SQLite database already lives in for this exact reason.
    public static class UpdateStateStore
    {
        private static readonly string LastUpdateCheckFile = Path.Combine(AppPaths.AppData, "last-update-check.txt");
        private static readonly string LastWhatsNewVersionFile = Path.Combine(AppPaths.AppData, "last-whats-new-version.txt");

        private static readonly string PendingApplyFile = Path.Combine(AppPaths.AppData, "pending-apply-version.txt");

        // Written just before the app hands over to the updater, read on the next launch: if the
        // installed version is still older than this, the swap did not happen.
        public static string? GetPendingApplyVersion()
        {
            try { return File.Exists(PendingApplyFile) ? File.ReadAllText(PendingApplyFile).Trim() : null; }
            catch { return null; }
        }

        public static void SetPendingApplyVersion(string? version)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(version)) File.Delete(PendingApplyFile);
                else File.WriteAllText(PendingApplyFile, version);
            }
            catch
            {
                // Best-effort - worst case a failed update is not announced.
            }
        }

        public static DateTime GetLastUpdateCheckUtc()
        {
            try
            {
                if (File.Exists(LastUpdateCheckFile) &&
                    DateTime.TryParse(File.ReadAllText(LastUpdateCheckFile), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var dt))
                {
                    return dt;
                }
            }
            catch
            {
                // Treat an unreadable/corrupt file as "never checked" - worst case, one extra check.
            }
            return DateTime.MinValue;
        }

        public static void SetLastUpdateCheckUtc(DateTime utc)
        {
            try
            {
                File.WriteAllText(LastUpdateCheckFile, utc.ToString("o", CultureInfo.InvariantCulture));
            }
            catch
            {
                // Best-effort - worst case the next launch checks a bit sooner than 24h.
            }
        }

        public static string? GetLastWhatsNewVersionShown()
        {
            try
            {
                return File.Exists(LastWhatsNewVersionFile) ? File.ReadAllText(LastWhatsNewVersionFile).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        public static void SetLastWhatsNewVersionShown(string version)
        {
            try
            {
                File.WriteAllText(LastWhatsNewVersionFile, version);
            }
            catch
            {
                // Best-effort - worst case the notes show once more than intended, not forever.
            }
        }
    }
}
