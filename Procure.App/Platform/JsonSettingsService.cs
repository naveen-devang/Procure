using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Procure.Models;
using Procure.Services;
using Procure.Services.Export;

namespace Procure.App.Platform;

/// <summary>
/// ISettingsService backed by a plain JSON file in the app data directory - the same
/// reasoning as UpdateStateStore (ApplicationData.LocalSettings is unreliable for an
/// unpackaged Velopack app). Replaces the MAUI Preferences-backed SettingsService.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path;
    private Dictionary<string, string> _values = new();

    // Held for every read and write of _values: the database layer asks for the saved folder from
    // background threads while the UI thread is saving a setting.
    private readonly object _gate = new();

    public string? LastSaveError { get; private set; }

    public JsonSettingsService() : this(Path.Combine(AppPaths.AppData, "settings.json"), wireDatabaseFolder: true) { }

    /// <summary>A service over any file. Only the real one wires the database-folder hooks - a test copy
    /// must not take them over.</summary>
    internal JsonSettingsService(string path, bool wireDatabaseFolder)
    {
        _path = path;
        Load();

        if (!wireDatabaseFolder) return;

        // The custom database directory was a MAUI Preference; back it with settings.json now.
        Procure.Data.DatabaseConstants.SavedDirectoryReader = () =>
        {
            lock (_gate)
                return _values.TryGetValue(nameof(DatabaseDirectory), out var d) && !string.IsNullOrWhiteSpace(d) ? d : null;
        };
        Procure.Data.DatabaseConstants.SavedDirectoryWriter = v => Set(nameof(DatabaseDirectory), v);
    }

    private string BackupPath => _path + ".bak";

    private static Dictionary<string, string>? TryRead(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                : null;
        }
        catch { return null; }
    }

    /// <summary>The file, else its last good copy, else a first run. A file that cannot be read is moved
    /// aside rather than overwritten by the next save: it holds the saved database folder, and losing that
    /// silently opens a new empty database.</summary>
    private void Load()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                var read = TryRead(_path);
                if (read is not null) { _values = read; return; }

                var kept = TryQuarantine();
                _values = TryRead(BackupPath) ?? new();
                Procure.Utilities.CrashLog.Write(_values.Count > 0
                    ? $"settings.json could not be read; restored the last good copy (damaged file kept as {kept})"
                    : $"settings.json could not be read and there is no good copy; settings are back to defaults (damaged file kept as {kept})");
                return;
            }

            if (TryRead(BackupPath) is { } fromBackup) { _values = fromBackup; return; }

            try { MigrateFromMauiPreferences(); }   // one-time, only before settings.json first exists
            catch { /* nothing to carry forward */ }
        }
    }

    private string? TryQuarantine()
    {
        try
        {
            var aside = Path.Combine(Path.GetDirectoryName(_path)!, $"settings.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(_path, aside);
            return Path.GetFileName(aside);
        }
        catch { return null; }
    }

    /// <summary>Writes the whole file: to a temp file first, then swapped in, with the previous file kept as
    /// .bak. A crash or a full disk mid-write can no longer leave half a file behind. Failure is recorded
    /// in <see cref="LastSaveError"/> and logged, never thrown into the setter that triggered it.</summary>
    private bool Persist()
    {
        lock (_gate)
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_values));
                if (File.Exists(_path)) File.Copy(_path, BackupPath, overwrite: true);
                File.Move(tmp, _path, overwrite: true);
                LastSaveError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                Procure.Utilities.CrashLog.Write("settings.json could not be saved", ex);
                return false;
            }
        }
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
        Persist();

        // Keyboard shortcut overrides lived under the same Preferences store.
        if (V("Procure_KeyboardShortcutOverrides") is { } ko && ko.Trim() is not ("" or "{}"))
        {
            try { File.WriteAllText(Path.Combine(AppPaths.AppData, "keyboard-shortcuts.json"), ko); } catch { }
        }
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    private string Get(string key, string fallback)
    {
        lock (_gate) return _values.TryGetValue(key, out var v) ? v : fallback;
    }

    private void Set(string key, string value, [System.Runtime.CompilerServices.CallerMemberName] string prop = "")
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var cur) && cur == value) return;
            _values[key] = value;
            Persist();
        }
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

    public string LocalCurrency
    {
        get => PcrCurrencyConversionOptions.Normalize(Get(nameof(LocalCurrency), DefaultCurrency));
        set => Set(nameof(LocalCurrency), PcrCurrencyConversionOptions.Normalize(value));
    }

    public IReadOnlyDictionary<string, decimal> CurrencyRates
    {
        get
        {
            var values = new Dictionary<string, decimal>(
                PcrCurrencyConversionOptions.DefaultRatesFor(LocalCurrency),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var raw = Get(nameof(CurrencyRates), "{}");
                var parsed = JsonSerializer.Deserialize<Dictionary<string, decimal>>(raw);
                if (parsed is not null)
                {
                    foreach (var entry in parsed.Where(x => x.Value > 0m))
                        values[PcrCurrencyConversionOptions.Normalize(entry.Key)] = entry.Value;
                }
            }
            catch
            {
                // Keep the editable built-in defaults when an older or malformed setting is found.
            }
            return values;
        }
    }

    public void SetCurrencyRate(string currency, decimal rate)
    {
        if (rate <= 0m) throw new ArgumentOutOfRangeException(nameof(rate));
        var normalized = PcrCurrencyConversionOptions.Normalize(currency);
        if (normalized.Length != 3) throw new ArgumentException("Currency code must have three letters.", nameof(currency));

        var values = new Dictionary<string, decimal>(CurrencyRates, StringComparer.OrdinalIgnoreCase)
        {
            [normalized] = rate
        };
        Set(nameof(CurrencyRates), JsonSerializer.Serialize(values), nameof(CurrencyRates));
    }

    public void RemoveCurrencyRate(string currency)
    {
        var normalized = PcrCurrencyConversionOptions.Normalize(currency);
        var values = new Dictionary<string, decimal>(CurrencyRates, StringComparer.OrdinalIgnoreCase);
        if (!values.Remove(normalized)) return;
        Set(nameof(CurrencyRates), JsonSerializer.Serialize(values), nameof(CurrencyRates));
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

    public bool IsServiceEntryTabEnabled
    {
        get => Get(nameof(IsServiceEntryTabEnabled), "false") == "true";
        set => Set(nameof(IsServiceEntryTabEnabled), value ? "true" : "false");
    }

    public string ServiceEntryColumnWidths
    {
        get => Get(nameof(ServiceEntryColumnWidths), "");
        set => Set(nameof(ServiceEntryColumnWidths), value);
    }

    public int ServiceEntryFlagDays
    {
        get => int.TryParse(Get(nameof(ServiceEntryFlagDays), "10"), out var n) && n > 0 ? n : 10;
        set => Set(nameof(ServiceEntryFlagDays), Math.Max(1, value).ToString());
    }

    public bool AutoCheckUpdatesOnStartup
    {
        get => Get(nameof(AutoCheckUpdatesOnStartup), "true") == "true";
        set => Set(nameof(AutoCheckUpdatesOnStartup), value ? "true" : "false");
    }

    public string DefaultReminderTime
    {
        get => Get(nameof(DefaultReminderTime), "09:00");
        set => Set(nameof(DefaultReminderTime), value);
    }

    public IReadOnlyList<PastelThemeOption> AvailableAccentThemes => AccentPalettes.All;

    public List<string> GetDefaultApprovalRoles()
    {
        var raw = Get("DefaultApprovalRoles", "");
        return string.IsNullOrWhiteSpace(raw)
            ? new List<string> { ApprovalRoles.ProcurementManager, ApprovalRoles.FinanceController, ApprovalRoles.Cfo, ApprovalRoles.Ceo }
            : new List<string>(raw.Split('|', StringSplitOptions.RemoveEmptyEntries));
    }

    public void SetDefaultApprovalRoles(IEnumerable<string> roles) =>
        Set("DefaultApprovalRoles", string.Join('|', roles));

    // Theme application is the host's job (WinUiAppHost.ApplyThemeAsync); App.OnLaunched
    // calls it once MainWindow is up.
    public void ApplySavedTheme() { }
}
