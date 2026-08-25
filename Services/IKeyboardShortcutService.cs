using System;

namespace Procure.Services
{
    public interface IKeyboardShortcutService
    {
        // Current binding for an action - the user's override if they've set one, else the registry
        // default. Combo strings look like "Ctrl+F" or "F5" - see Utilities.ShortcutInput.Capture.
        string GetCombo(string actionId);

        bool IsCustomized(string actionId);

        void SetCombo(string actionId, string combo);

        void ResetToDefault(string actionId);

        void ResetAllToDefaults();

        // Null when nothing is being captured; the id of the action awaiting its next keystroke
        // while the Settings page's recorder is active. Lives here (not on the Settings page model)
        // because the global keyboard hook that has to check it lives outside any single page.
        string? RecordingActionId { get; set; }

        // The id of another action already bound to the given combo, or null if it's free.
        string? FindConflict(string combo, string excludingActionId);

        event EventHandler? ShortcutsChanged;
        event EventHandler? RecordingActionChanged;
    }
}
