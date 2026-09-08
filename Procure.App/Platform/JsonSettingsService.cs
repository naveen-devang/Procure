using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Procure.Models;
using Procure.Services;

namespace Procure.App.Platform;

/// <summary>
/// ISettingsService backed by a plain JSON file in the app data directory - the same
/// reasoning as UpdateStateStore (ApplicationData.LocalSettings is unreliable for an
/// unpackaged Velopack app). Replaces the MAUI Preferences-backed SettingsService.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path = Path.Combine(AppPaths.AppData, "settings.json");
    private readonly Dictionary<string, string> _values = new();

    public JsonSettingsService()
    {
        try
        {
            if (File.Exists(_path))
                _values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
        }
        catch { /* corrupt file -> defaults */ }
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    private string Get(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : fallback;

    private void Set(string key, string value, [System.Runtime.CompilerServices.CallerMemberName] string prop = "")
    {
        if (_values.TryGetValue(key, out var cur) && cur == value) return;
        _values[key] = value;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_values)); } catch { }
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(prop));
    }

    public int UrgentOverdueDays
    {
        get => int.TryParse(Get(nameof(UrgentOverdueDays), "5"), out var n) ? n : 5;
        set => Set(nameof(UrgentOverdueDays), value.ToString());
    }

    public int NormalOverdueDays
    {
        get => int.TryParse(Get(nameof(NormalOverdueDays), "10"), out var n) ? n : 10;
        set => Set(nameof(NormalOverdueDays), value.ToString());
    }

    public string DatabaseDirectory
    {
        get => Procure.Data.DatabaseConstants.DatabaseDirectory;
        set => Procure.Data.DatabaseConstants.DatabaseDirectory = value;
    }

    public string AppTheme
    {
        get => Get(nameof(AppTheme), "System");
        set => Set(nameof(AppTheme), value);
    }

    public string AccentTheme
    {
        get => Get(nameof(AccentTheme), "Blue");
        set => Set(nameof(AccentTheme), value);
    }

    public string DefaultCurrency
    {
        get => Get(nameof(DefaultCurrency), "AED");
        set => Set(nameof(DefaultCurrency), value);
    }

    public bool IsSidebarCompact
    {
        get => Get(nameof(IsSidebarCompact), "false") == "true";
        set => Set(nameof(IsSidebarCompact), value ? "true" : "false");
    }

    public bool AutoCollapseSidebarOnNarrow
    {
        get => Get(nameof(AutoCollapseSidebarOnNarrow), "true") == "true";
        set => Set(nameof(AutoCollapseSidebarOnNarrow), value ? "true" : "false");
    }

    public bool IsRawPackingTabEnabled
    {
        get => Get(nameof(IsRawPackingTabEnabled), "false") == "true";
        set => Set(nameof(IsRawPackingTabEnabled), value ? "true" : "false");
    }

    public bool AutoCheckUpdatesOnStartup
    {
        get => Get(nameof(AutoCheckUpdatesOnStartup), "true") == "true";
        set => Set(nameof(AutoCheckUpdatesOnStartup), value ? "true" : "false");
    }

    public IReadOnlyList<PastelThemeOption> AvailableAccentThemes => AccentPalettes.All;

    public List<string> GetDefaultApprovalRoles()
    {
        var raw = Get("DefaultApprovalRoles", "");
        return string.IsNullOrWhiteSpace(raw)
            ? new List<string> { "Requestor", "Department Head", "Finance", "Management" }
            : new List<string>(raw.Split('|', StringSplitOptions.RemoveEmptyEntries));
    }

    public void SetDefaultApprovalRoles(IEnumerable<string> roles) =>
        Set("DefaultApprovalRoles", string.Join('|', roles));

    // Theme application is the host's job (WinUiAppHost.ApplyThemeAsync); App.OnLaunched
    // calls it once MainWindow is up.
    public void ApplySavedTheme() { }
}
