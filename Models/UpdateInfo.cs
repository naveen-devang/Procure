using System;

namespace Procure.Models
{
    public class UpdateInfo
    {
        public string TagName { get; set; } = string.Empty;
        public Version? Version { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string ReleaseUrl { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string AssetName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime? PublishedAt { get; set; }
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersionString { get; set; } = string.Empty;
        public string LatestVersionString { get; set; } = string.Empty;

        public string FormattedSize
        {
            get
            {
                if (SizeBytes <= 0) return string.Empty;
                double mb = SizeBytes / (1024.0 * 1024.0);
                return $"{mb:F1} MB";
            }
        }
    }
}
