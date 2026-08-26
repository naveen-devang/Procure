using System;
using System.Collections.Generic;
using Procure.Models;

namespace Procure.Services
{
    public sealed class SettingsChangedEventArgs(string key) : EventArgs
    {
        public string Key { get; } = key;
    }

    public interface ISettingsService
    {
        int UrgentOverdueDays { get; set; }
        int NormalOverdueDays { get; set; }
        string DatabaseDirectory { get; set; }
        string AppTheme { get; set; } // System, Light, Dark
        string AccentTheme { get; set; } // Blue, Purple, Mint, Coral, Pink, Red, Yellow, Teal
        string DefaultCurrency { get; set; } // AED, USD, EUR, etc.
        bool IsSidebarCompact { get; set; }
        bool AutoCollapseSidebarOnNarrow { get; set; }
        bool IsRawPackingTabEnabled { get; set; }
        bool AutoCheckUpdatesOnStartup { get; set; }
        IReadOnlyList<PastelThemeOption> AvailableAccentThemes { get; }
        List<string> GetDefaultApprovalRoles();
        void SetDefaultApprovalRoles(IEnumerable<string> roles);
        void ApplySavedTheme();
        event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
    }
}
