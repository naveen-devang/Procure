using System;
using System.Threading;
using System.Threading.Tasks;
using Procure.Models;

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
    }
}
