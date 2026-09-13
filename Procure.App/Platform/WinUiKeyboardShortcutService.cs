using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Procure.Services;
using Procure.Utilities;

namespace Procure.App.Platform;

// WinUI port of the MAUI KeyboardShortcutService. Same logic; the override map is
// persisted to a plain JSON file in AppPaths.AppData instead of MAUI Preferences
// (same reasoning as JsonSettingsService).
public sealed class WinUiKeyboardShortcutService : IKeyboardShortcutService
{
    private readonly string _path = Path.Combine(Procure.AppPaths.AppData, "keyboard-shortcuts.json");
    private Dictionary<string, string>? _overrides;
    private string? _recordingActionId;

    public event EventHandler? ShortcutsChanged;
    public event EventHandler? RecordingActionChanged;

    private Dictionary<string, string> Overrides => _overrides ??= Load();

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
        }
        catch { /* corrupt/pre-release value -> defaults */ }
        return new Dictionary<string, string>();
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Overrides)); } catch { }
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetCombo(string actionId) =>
        Overrides.TryGetValue(actionId, out var combo) ? combo : KeyboardShortcutRegistry.Get(actionId).DefaultCombo;

    public bool IsCustomized(string actionId) => Overrides.ContainsKey(actionId);

    public void SetCombo(string actionId, string combo)
    {
        if (string.Equals(GetCombo(actionId), combo, StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(KeyboardShortcutRegistry.Get(actionId).DefaultCombo, combo, StringComparison.OrdinalIgnoreCase))
            Overrides.Remove(actionId);
        else
            Overrides[actionId] = combo;
        Save();
    }

    public void ResetToDefault(string actionId)
    {
        if (!Overrides.Remove(actionId)) return;
        Save();
    }

    public void ResetAllToDefaults()
    {
        if (Overrides.Count == 0) return;
        Overrides.Clear();
        Save();
    }

    public string? RecordingActionId
    {
        get => _recordingActionId;
        set
        {
            if (_recordingActionId == value) return;
            _recordingActionId = value;
            RecordingActionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? FindConflict(string combo, string excludingActionId)
    {
        foreach (var def in KeyboardShortcutRegistry.All)
        {
            if (def.Id == excludingActionId) continue;
            if (string.Equals(GetCombo(def.Id), combo, StringComparison.OrdinalIgnoreCase)) return def.Id;
        }
        return null;
    }
}
