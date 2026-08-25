using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Maui.Storage;
using Procure.Utilities;

namespace Procure.Services
{
    public class KeyboardShortcutService : IKeyboardShortcutService
    {
        private const string PrefsKey = "Procure_KeyboardShortcutOverrides";
        private Dictionary<string, string>? _overrides;
        private string? _recordingActionId;

        public event EventHandler? ShortcutsChanged;
        public event EventHandler? RecordingActionChanged;

        private Dictionary<string, string> Overrides => _overrides ??= Load();

        private static Dictionary<string, string> Load()
        {
            var raw = Preferences.Default.Get(PrefsKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, string>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new Dictionary<string, string>();
            }
            catch
            {
                // A corrupted or pre-release value should fall back to defaults, not crash Settings.
                return new Dictionary<string, string>();
            }
        }

        private void Save()
        {
            Preferences.Default.Set(PrefsKey, JsonSerializer.Serialize(Overrides));
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetCombo(string actionId) =>
            Overrides.TryGetValue(actionId, out var combo) ? combo : KeyboardShortcutRegistry.Get(actionId).DefaultCombo;

        public bool IsCustomized(string actionId) => Overrides.ContainsKey(actionId);

        public void SetCombo(string actionId, string combo)
        {
            if (string.Equals(GetCombo(actionId), combo, StringComparison.OrdinalIgnoreCase)) return;

            // Setting it back to the registry default un-customizes it rather than storing a
            // redundant override - keeps "Reset All" and the customized-indicator dot both honest.
            if (string.Equals(KeyboardShortcutRegistry.Get(actionId).DefaultCombo, combo, StringComparison.OrdinalIgnoreCase))
            {
                Overrides.Remove(actionId);
            }
            else
            {
                Overrides[actionId] = combo;
            }
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
}
