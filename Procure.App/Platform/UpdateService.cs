using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;
using Procure.Services;
using Procure.Utilities;
using UpdateInfo = Procure.Models.UpdateInfo;
using VelopackUpdateInfo = Velopack.UpdateInfo;

namespace Procure.App.Platform;

// WinUI port of the MAUI UpdateService. Identical Velopack-backed logic; the only MAUI
// dependency was Launcher.Default.OpenAsync for "open the release page", now Process.Start.
public sealed class UpdateService : IUpdateService
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ILogger<UpdateService>? _logger;

    // Lazy + guarded: on an unpackaged dev build there is no VelopackLocator, and the
    // UpdateManager ctor throws. That is not an error - there is just nothing to update.
    private readonly Lazy<UpdateManager?> _managerLazy = new(() =>
    {
        try
        {
            return new UpdateManager(new GithubSource(
                $"https://github.com/{Procure.AppConstants.GitHubRepository}", null, prerelease: false));
        }
        catch
        {
            return null;
        }
    });

    private UpdateManager? Mgr => _managerLazy.Value;

    private VelopackUpdateInfo? _pendingUpdate;

    private readonly UpdateDownloadCoordinator _download = new();
    private volatile bool _isChecking;

    public UpdateDownloadStatus DownloadStatus => _download.Status;
    public double DownloadProgress => _download.Progress;
    public string? PendingUpdateTag => _download.VersionTag;
    public UpdateInfo? LastKnownUpdate { get; private set; }
    public bool IsUpdateBusy => _isChecking || _download.IsRunning;

    public event EventHandler? UpdateStateChanged;

    private void RaiseState() => UpdateStateChanged?.Invoke(this, EventArgs.Empty);

    public string CurrentVersionString =>
        Mgr is { IsInstalled: true, CurrentVersion: { } cv }
            ? cv.ToString()
            : "Dev build";

    public Version CurrentVersion =>
        Version.TryParse(CurrentVersionString, out var v) ? v : new Version(1, 0, 0);

    public UpdateService(ILogger<UpdateService>? logger = null)
    {
        _logger = logger;
        _download.Changed += (_, _) => RaiseState();
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

        _isChecking = true;
        RaiseState();
        try
        {
            if (Mgr is not { IsInstalled: true } mgr)
            {
                _logger?.LogInformation("Velopack reports app is not installed - skipping update check.");
                return result;
            }

            _pendingUpdate = await mgr.CheckForUpdatesAsync();
            if (_pendingUpdate == null)
            {
                return result;
            }

            var asset = _pendingUpdate.TargetFullRelease;
            result.TagName = $"v{asset.Version}";
            result.Title = result.TagName;
            result.ReleaseNotes = asset.NotesMarkdown ?? string.Empty;
            result.ReleaseUrl = $"https://github.com/{repoOwnerAndName.Trim().Trim('/')}/releases/tag/{result.TagName}";
            result.DownloadUrl = result.ReleaseUrl;
            result.LatestVersionString = asset.Version.ToString();

            var deltas = _pendingUpdate.DeltasToTarget?.ToArray() ?? Array.Empty<VelopackAsset>();
            if (deltas.Length > 0)
            {
                result.SizeBytes = deltas.Sum(d => d.Size);
                result.AssetName = deltas[^1].FileName;
                result.IsDeltaDownload = true;
            }
            else
            {
                result.SizeBytes = asset.Size;
                result.AssetName = asset.FileName;
            }
            result.IsUpdateAvailable = true;
            if (Version.TryParse(asset.Version.ToString().Split('-')[0], out var v))
            {
                result.Version = v;
            }

            LastKnownUpdate = result;
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to check for updates on {Repo}", repoOwnerAndName);
            throw;
        }
        finally
        {
            _isChecking = false;
            RaiseState();
        }
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (_pendingUpdate == null || Mgr is not { } mgr)
        {
            throw new InvalidOperationException("No pending update to download - call CheckForUpdatesAsync first.");
        }

        var pending = _pendingUpdate;
        var deltaCount = pending.DeltasToTarget?.Length ?? 0;
        var tag = string.IsNullOrWhiteSpace(update.TagName) ? $"v{pending.TargetFullRelease.Version}" : update.TagName;

        await _download.RunAsync(tag, async (coordProgress, token) =>
        {
            var sw = Stopwatch.StartNew();
            await mgr.DownloadUpdatesAsync(pending, p =>
            {
                var f = p / 100.0;
                coordProgress.Report(f);
                progress?.Report(f);
            }, token).ConfigureAwait(false);
            sw.Stop();

            try
            {
                var expected = update.IsDeltaDownload
                    ? $"delta x{deltaCount}, ~{update.SizeBytes / 1048576.0:F1} MB"
                    : $"full, ~{update.SizeBytes / 1048576.0:F1} MB";
                CrashLog.Write($"UPDATE DOWNLOAD {tag}: {expected}, took {sw.Elapsed.TotalSeconds:F1}s");
            }
            catch { /* logging must never break the update */ }
        }, ct).ConfigureAwait(false);

        progress?.Report(1.0);
        return "velopack-update-ready";
    }

    public bool LaunchInstaller(string installerPath)
    {
        if (_pendingUpdate == null || Mgr is not { } mgr)
        {
            _logger?.LogError("LaunchInstaller called with no pending Velopack update.");
            return false;
        }

        try
        {
            mgr.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
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

    public Task OpenReleaseInBrowserAsync(string releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl)) return Task.CompletedTask;
        try
        {
            Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open release URL in browser: {Url}", releaseUrl);
        }
        return Task.CompletedTask;
    }
}
