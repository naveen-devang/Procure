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
            else
                MigrateFromMauiPreferences();   // one-time, only before settings.json first exists
        }
        catch { /* corrupt file -> defaults */ }

        // The custom database directory was a MAUI Preference; back it with settings.json now.
        Procure.Data.DatabaseConstants.SavedDirectoryReader = () =>
            _values.TryGetValue(nameof(DatabaseDirectory), out var d) && !string.IsNullOrWhiteSpace(d) ? d : null;
        Procure.Data.DatabaseConstants.SavedDirectoryWriter = v => Set(nameof(DatabaseDirectory), v);
    }

    // MAUI stored settings via Preferences, which on unpackaged Windows is this JSON file:
    //   %LOCALAPPDATA%\User Name\com.companyname.procure\Settings\preferences.dat  ->  {"":{ "<key>": "<string>" }}
    // On a colleague's first launch of the WinUI build, carry those forward so their theme,
    // accent, currency, tab toggles, approval roles and custom DB path don't silently reset.
    private void MigrateFromMauiPreferences()
    {
        var dat = Path.Combine(AppPaths.AppData, "..", "Settings", "preferences.dat");
        if (!File.Exists(dat)) return;

        Dictionary<string, string> old;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dat));
            if (!doc.RootElement.TryGetProperty("", out var bucket)) return;
            old = new();
            foreach (var p in bucket.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) old[p.Name] = p.Value.GetString()!;
        }
        catch { return; }

        string? V(string k) => old.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        void Copy(string mauiKey, string jsonKey, Func<string, string>? map = null)
        {
            if (V(mauiKey) is { } v) _values[jsonKey] = map is null ? v : map(v);
        }
        string Lower(string s) => s.Trim().ToLowerInvariant();     // MAUI writes bools as "True"/"False"

        Copy("Procure_AppTheme", nameof(AppTheme));
        Copy("Procure_AccentTheme", nameof(AccentTheme));
        Copy("Procure_DefaultCurrency", nameof(DefaultCurrency));
        Copy("Procure_UrgentDays", nameof(UrgentOverdueDays));
        Copy("Procure_NormalDays", nameof(NormalOverdueDays));
        Copy("Procure_IsSidebarCompact", nameof(IsSidebarCompact), Lower);
        Copy("Procure_SidebarCompact", nameof(IsSidebarCompact), Lower);   // older key name, if the newer is absent
        Copy("Procure_AutoCollapseOnNarrow", nameof(AutoCollapseSidebarOnNarrow), Lower);
        Copy("Procure_RawPackingTabEnabled", nameof(IsRawPackingTabEnabled), Lower);
        Copy("Procure_AutoCheckUpdates", nameof(AutoCheckUpdatesOnStartup), Lower);
        Copy("Procure_DefaultApprovalRoles", "DefaultApprovalRoles",
             v => string.Join('|', v.Split("|||", StringSplitOptions.RemoveEmptyEntries)));
        Copy("CustomDatabaseDirectory", nameof(DatabaseDirectory));

        if (_values.Count == 0) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_values)); } catch { }

        // Keyboard shortcut overrides lived under the same Preferences store.
        if (V("Procure_KeyboardShortcutOverrides") is { } ko && ko.Trim() is not ("" or "{}"))
        {
            try { File.WriteAllText(Path.Combine(AppPaths.AppData, "keyboard-shortcuts.json"), ko); } catch { }
        }
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
