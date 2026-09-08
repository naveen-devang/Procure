using System;
using System.Threading;
using System.Threading.Tasks;
using Procure.Models;
using Procure.Utilities;

namespace Procure.Services
{
    public interface IUpdateService
    {
        string CurrentVersionString { get; }
        Version CurrentVersion { get; }
        Task<UpdateInfo> CheckForUpdatesAsync(string repoOwnerAndName);
        Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);
        bool LaunchInstaller(string installerPath);
        Task OpenReleaseInBrowserAsync(string releaseUrl);

        // Shared download state - one owner, so the startup auto-download and the Settings button
        // can't run two Velopack fetches at once, and Settings can show a background download live.
        UpdateDownloadStatus DownloadStatus { get; }
        double DownloadProgress { get; }
        string? PendingUpdateTag { get; }
        UpdateInfo? LastKnownUpdate { get; }
        bool IsUpdateBusy { get; }
        event EventHandler? UpdateStateChanged;

        // Independent of the Velopack-backed check above (which only looks at the *latest*
        // available release) - this looks up the notes for whatever version is currently
        // running, for the one-time "What's New" prompt shown right after an update lands.
        Task<string?> GetReleaseNotesForVersionAsync(string repoOwnerAndName, string version);
    }
}
