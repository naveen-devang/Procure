using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Velopack;
using Velopack.Sources;
using UpdateInfo = Procure.Models.UpdateInfo;
using VelopackUpdateInfo = Velopack.UpdateInfo;

namespace Procure.Services
{
    // Backed by Velopack instead of hand-rolled GitHub API calls + ShellExecute. The old
    // implementation could check for and download a release, but had no way to actually apply
    // it - a running Windows app can't overwrite its own files, and "launch whatever got
    // downloaded" only works if that happens to be a real installer. Velopack's
    // ApplyUpdatesAndRestart does that safely (exit, swap files, relaunch), and its GithubSource
    // reads the same public GitHub Releases feed this always pointed at.
    //
    // IUpdateService's shape is unchanged on purpose, so PageModels/SettingsPageModel.cs and its
    // Settings UI didn't need to change at all - only what happens underneath each call did.
    public class UpdateService : IUpdateService
    {
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly ILogger<UpdateService>? _logger;
        private UpdateManager? _manager;
        private VelopackUpdateInfo? _pendingUpdate;

        public string CurrentVersionString =>
            _manager?.IsInstalled == true && _manager.CurrentVersion != null
                ? _manager.CurrentVersion.ToString()
                : AppInfo.Current.VersionString;

        public Version CurrentVersion
        {
            get
            {
                if (Version.TryParse(CurrentVersionString, out var v)) return v;
                return new Version(1, 0, 0);
            }
        }

        public UpdateService(ILogger<UpdateService>? logger = null)
        {
            _logger = logger;
        }

        private UpdateManager GetManager(string repoOwnerAndName)
        {
            // Built lazily against the repo passed to CheckForUpdatesAsync (always
            // AppConstants.GitHubRepository in practice) rather than at construction, since the
            // interface never gave a constructor-time place to know it. The repo is public, so
            // no access token is needed here.
            var repoUrl = $"https://github.com/{repoOwnerAndName.Trim().Trim('/')}";
            return _manager ??= new UpdateManager(new GithubSource(repoUrl, null, prerelease: false));
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync(string repoOwnerAndName)
        {
            var result = new UpdateInfo
            {
                CurrentVersionString = CurrentVersionString,
                IsUpdateAvailable = false
            };

            if (string.IsNullOrWhiteSpace(repoOwnerAndName) || !repoOwnerAndName.Contains('/'))
            {
                _logger?.LogWarning("Invalid repository name format for update check: {Repo}", repoOwnerAndName);
                return result;
            }

            try
            {
                var manager = GetManager(repoOwnerAndName);

                if (!manager.IsInstalled)
                {
                    // Running unpackaged (e.g. F5 in the debugger, or a manually copied publish
                    // folder) - Velopack has nothing to check against. Not an error, just nothing
                    // to report.
                    _logger?.LogInformation("Velopack reports app is not installed - skipping update check.");
                    return result;
                }

                _pendingUpdate = await manager.CheckForUpdatesAsync();
                if (_pendingUpdate == null)
                {
                    return result;
                }

                var asset = _pendingUpdate.TargetFullRelease;
                result.TagName = $"v{asset.Version}";
                result.Title = result.TagName;
                result.ReleaseNotes = asset.NotesMarkdown ?? string.Empty;
                result.ReleaseUrl = $"https://github.com/{repoOwnerAndName.Trim().Trim('/')}/releases/tag/{result.TagName}";
                // Velopack downloads internally (DownloadUpdatesAsync/ApplyUpdatesAndRestart below)
                // rather than needing a raw HTTP URL - but SettingsPageModel.DownloadAndInstallUpdateAsync
                // treats a blank DownloadUrl as "nothing to download, open the release page instead",
                // a fallback written for the old implementation. Any non-empty value keeps it on the
                // real download path; the release URL is the most meaningful thing to put there.
                result.DownloadUrl = result.ReleaseUrl;
                result.LatestVersionString = asset.Version.ToString();
                result.AssetName = asset.FileName;
                result.SizeBytes = asset.Size;
                result.IsUpdateAvailable = true;
                if (Version.TryParse(asset.Version.ToString().Split('-')[0], out var v))
                {
                    result.Version = v;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to check for updates on {Repo}", repoOwnerAndName);
                throw;
            }
        }

        public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (_manager == null || _pendingUpdate == null)
            {
                throw new InvalidOperationException("No pending update to download - call CheckForUpdatesAsync first.");
            }

            await _manager.DownloadUpdatesAsync(_pendingUpdate, p => progress?.Report(p / 100.0), ct);
            progress?.Report(1.0);

            // Velopack tracks the downloaded package itself; there's no installer file path for
            // the caller to do anything with. This return value only exists so LaunchInstaller
            // (below) has a non-null argument to receive, per the unchanged interface shape.
            return "velopack-update-ready";
        }

        public bool LaunchInstaller(string installerPath)
        {
            if (_manager == null || _pendingUpdate == null)
            {
                _logger?.LogError("LaunchInstaller called with no pending Velopack update.");
                return false;
            }

            try
            {
                // Exits the app, swaps in the new files, and relaunches - the actual "install"
                // step the old ShellExecute-based version never had.
                _manager.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to apply Velopack update.");
                return false;
            }
        }

        public async Task<string?> GetReleaseNotesForVersionAsync(string repoOwnerAndName, string version)
        {
            if (string.IsNullOrWhiteSpace(repoOwnerAndName) || string.IsNullOrWhiteSpace(version))
                return null;

            var tag = version.StartsWith('v') ? version : $"v{version}";
            var url = $"https://api.github.com/repos/{repoOwnerAndName.Trim().Trim('/')}/releases/tags/{tag}";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Procure-App", version));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return doc.RootElement.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to fetch release notes for {Version}", version);
                return null;
            }
        }

        public async Task OpenReleaseInBrowserAsync(string releaseUrl)
        {
            if (string.IsNullOrWhiteSpace(releaseUrl)) return;
            try
            {
                await Launcher.Default.OpenAsync(new Uri(releaseUrl));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to open release URL in browser: {Url}", releaseUrl);
            }
        }
    }
}
