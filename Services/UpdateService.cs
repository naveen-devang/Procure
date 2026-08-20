using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Procure.Models;

namespace Procure.Services
{
    public class UpdateService : IUpdateService
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly ILogger<UpdateService>? _logger;

        public string CurrentVersionString => AppInfo.Current.VersionString;

        public Version CurrentVersion
        {
            get
            {
                if (Version.TryParse(AppInfo.Current.VersionString, out var v))
                {
                    return v;
                }
                return new Version(1, 0, 0);
            }
        }

        public UpdateService(ILogger<UpdateService>? logger = null)
        {
            _logger = logger;
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync(string repoOwnerAndName)
        {
            var updateInfo = new UpdateInfo
            {
                CurrentVersionString = CurrentVersionString,
                IsUpdateAvailable = false
            };

            if (string.IsNullOrWhiteSpace(repoOwnerAndName) || !repoOwnerAndName.Contains('/'))
            {
                _logger?.LogWarning("Invalid repository name format for update check: {Repo}", repoOwnerAndName);
                return updateInfo;
            }

            var cleanRepo = repoOwnerAndName.Trim().Trim('/');
            var url = $"https://api.github.com/repos/{cleanRepo}/releases/latest";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Procure-App", CurrentVersionString));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            try
            {
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger?.LogInformation("No GitHub releases found for repository: {Repo}", cleanRepo);
                    return updateInfo;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
                var title = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
                var htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? "" : "";

                DateTime? publishedAt = null;
                if (root.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTime(out var dt))
                {
                    publishedAt = dt;
                }

                updateInfo.TagName = tagName;
                updateInfo.Title = string.IsNullOrWhiteSpace(title) ? tagName : title;
                updateInfo.ReleaseNotes = body;
                updateInfo.ReleaseUrl = htmlUrl;
                updateInfo.PublishedAt = publishedAt;

                var cleanTag = tagName.TrimStart('v', 'V').Trim();
                updateInfo.LatestVersionString = cleanTag;

                if (TryParseSemanticVersion(cleanTag, out var latestVer))
                {
                    updateInfo.Version = latestVer;
                    updateInfo.IsUpdateAvailable = latestVer > CurrentVersion;
                }
                else
                {
                    updateInfo.IsUpdateAvailable = !string.IsNullOrWhiteSpace(cleanTag) &&
                                                   !cleanTag.Equals(CurrentVersionString, StringComparison.OrdinalIgnoreCase);
                }

                // Locate installer asset if available (.exe, .msix, .msi, .zip)
                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var downloadUrl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                        var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                        if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            assetName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ||
                            assetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            updateInfo.AssetName = assetName;
                            updateInfo.DownloadUrl = downloadUrl;
                            updateInfo.SizeBytes = size;
                            break; // Priority to native installers
                        }

                        if (string.IsNullOrEmpty(updateInfo.DownloadUrl) &&
                            assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            updateInfo.AssetName = assetName;
                            updateInfo.DownloadUrl = downloadUrl;
                            updateInfo.SizeBytes = size;
                        }
                    }
                }

                return updateInfo;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to check for GitHub updates on {Repo}", cleanRepo);
                throw;
            }
        }

        public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            {
                throw new InvalidOperationException("No download URL available for this release asset.");
            }

            var tempDir = FileSystem.CacheDirectory;
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            var fileName = !string.IsNullOrWhiteSpace(update.AssetName)
                ? update.AssetName
                : $"Procure-Update-{update.TagName}.exe";

            var destinationPath = Path.Combine(tempDir, fileName);

            using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Procure-App", CurrentVersionString));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? update.SizeBytes;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    var percentage = (double)totalRead / totalBytes;
                    progress.Report(percentage);
                }
            }

            progress?.Report(1.0);
            return destinationPath;
        }

        public bool LaunchInstaller(string installerPath)
        {
            try
            {
                if (!File.Exists(installerPath)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                };

                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to launch installer process: {Path}", installerPath);
                return false;
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

        private static bool TryParseSemanticVersion(string input, out Version version)
        {
            version = new Version(1, 0, 0);
            if (string.IsNullOrWhiteSpace(input)) return false;

            var clean = input.Split('-')[0].Trim(); // Remove prerelease tags like -beta
            var parts = clean.Split('.');

            if (parts.Length == 1 && int.TryParse(parts[0], out var major))
            {
                version = new Version(major, 0, 0);
                return true;
            }
            if (parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out var minor))
            {
                version = new Version(major, minor, 0);
                return true;
            }

            if (Version.TryParse(clean, out var parsedVersion) && parsedVersion != null)
            {
                version = parsedVersion;
                return true;
            }

            version = new Version(1, 0, 0);
            return false;
        }
    }
}
