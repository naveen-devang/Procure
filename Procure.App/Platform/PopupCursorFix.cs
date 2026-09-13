using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace Procure.App.Platform;

/// <summary>
/// Removes the busy (Wait) cursor that shows when a dropdown or date picker opens.
///
/// Measured, not guessed: WinUI draws its cursors through the input site and never through
/// SetCursor, while this thread's own current cursor is IDC_WAIT - from start-up, and again every
/// time a popup opens. Dropdowns, date pickers and flyouts are separate popup windows
/// (Microsoft.UI.Content.PopupWindowSiteBridge) whose window class has no cursor, so Windows paints
/// the thread's current cursor over them - Wait - until XAML handles a pointer event there: 60 ms
/// to 1.5 s with the pointer still. It reproduces in a blank one-ComboBox WinUI app too (bundled or
/// system runtime, SDK 1.8 or 2.4), so it is not in our XAML.
///
/// Fix: listen for our own windows being shown and put the arrow back as the thread cursor. The
/// hook is out-of-context and scoped to this process, so it is delivered on the UI thread's
/// message loop and costs nothing when no window is appearing.
/// </summary>
internal static class PopupCursorFix
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private const int OBJID_WINDOW = 0;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    // Held for the life of the process: a collected delegate behind a native hook is a crash.
    private static WinEventProc? _proc;
    private static IntPtr _hook;
    private static DispatcherQueue? _queue;

    public static void Install(DispatcherQueue queue)
    {
        if (_hook != IntPtr.Zero) return;
        _queue = queue;
        _proc = OnEvent;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc,
            (uint)Environment.ProcessId, 0, WINEVENT_OUTOFCONTEXT);
        Arrow();
    }

    private static void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        if (!sb.ToString().StartsWith("Microsoft.UI.Content.PopupWindowSiteBridge", StringComparison.Ordinal)) return;

        Arrow();
        // The popup can set Wait again while it finishes opening; once more after that has run.
        _queue?.TryEnqueue(DispatcherQueuePriority.Low, Arrow);
    }

    private static void Arrow() => SetCursor(LoadCursor(IntPtr.Zero, 32512 /* IDC_ARROW */));

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
}
