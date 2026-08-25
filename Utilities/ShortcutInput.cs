using System;
using System.Collections.Generic;

namespace Procure.Utilities
{
    // WinUI-specific half of the shortcut system: reading live modifier state and turning a
    // KeyRoutedEventArgs into the same "Ctrl+F"-style string KeyboardShortcutRegistry stores.
    // Kept separate from IKeyboardShortcutService so that service stays plain strings.
    public static class ShortcutInput
    {
        public static bool IsCtrlDown() =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        public static bool IsShiftDown() =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        public static bool IsAltDown() =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Modifier keys pressed alone never form a usable combo - the recorder waits for the real key.
        public static bool IsModifierKey(Windows.System.VirtualKey key) => key is
            Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftShift or Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows;

        // A handful of punctuation keys aren't named members of VirtualKey in every SDK projection;
        // the underlying values (standard Win32 VK_OEM_* codes) are stable regardless, so casting the
        // raw int is safe even where the enum has no matching name.
        private static string KeyName(Windows.System.VirtualKey key) => (int)key switch
        {
            188 => "Comma",  // VK_OEM_COMMA
            187 => "Plus",   // VK_OEM_PLUS
            189 => "Minus",  // VK_OEM_MINUS
            _ => key.ToString()
        };

        public static string Capture(Windows.System.VirtualKey key)
        {
            var parts = new List<string>();
            if (IsCtrlDown()) parts.Add("Ctrl");
            if (IsAltDown()) parts.Add("Alt");
            if (IsShiftDown()) parts.Add("Shift");
            parts.Add(KeyName(key));
            return string.Join("+", parts);
        }

        public static bool Matches(string combo, Windows.System.VirtualKey key) =>
            !string.IsNullOrWhiteSpace(combo) && string.Equals(combo, Capture(key), StringComparison.OrdinalIgnoreCase);
    }
}
