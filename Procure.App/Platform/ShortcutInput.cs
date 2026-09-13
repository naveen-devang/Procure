using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Procure.App.Platform;

/// <summary>
/// Turns a key press into the "Ctrl+F"-style string KeyboardShortcutRegistry stores. Same as the MAUI
/// app's Utilities/ShortcutInput, so saved bindings mean the same thing in both.
/// </summary>
internal static class ShortcutInput
{
    private static bool Down(VirtualKey k) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    // Modifier keys pressed alone never form a usable combo - the recorder waits for the real key.
    public static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    // VK_OEM_* punctuation has no VirtualKey name in every projection; the Win32 codes are stable.
    private static string KeyName(VirtualKey key) => (int)key switch
    {
        188 => "Comma",
        187 => "Plus",
        189 => "Minus",
        _ => key.ToString()
    };

    public static string Capture(VirtualKey key)
    {
        var parts = new List<string>();
        if (Down(VirtualKey.Control)) parts.Add("Ctrl");
        if (Down(VirtualKey.Menu)) parts.Add("Alt");
        if (Down(VirtualKey.Shift)) parts.Add("Shift");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static bool Matches(string combo, VirtualKey key) =>
        !string.IsNullOrWhiteSpace(combo) && string.Equals(combo, Capture(key), StringComparison.OrdinalIgnoreCase);

    // Ctrl+A, the arrow keys and Delete mean something inside a text field; shortcuts using them back off.
    public static bool IsTextInputFocused(XamlRoot? root) =>
        root != null && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox;
}
