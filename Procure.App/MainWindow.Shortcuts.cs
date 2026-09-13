using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Procure.Abstractions;
using Procure.App.Platform;
using Procure.App.Views;
using Procure.PageModels;
using Procure.Services;
using Procure.Utilities;
using Windows.System;

namespace Procure.App;

// Keyboard shortcuts. One hook on the window's root sees every key before the focused control does
// (PreviewKeyDown tunnels root to focus), so the global ones work on every page. Page-specific keys
// go to whichever page is showing. Only recognised keys are marked handled; everything else carries
// on to the control as normal. Ported from the MAUI app's AppShell and page hooks.
public sealed partial class MainWindow
{
    private IKeyboardShortcutService? _shortcuts;

    private void InstallShortcuts()
    {
        _shortcuts = App.Services.GetRequiredService<IKeyboardShortcutService>();
        if (Content is not UIElement root) return;
        root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRootPreviewKeyDown), handledEventsToo: true);
    }

    // Keys only travel along the path to the focused element; with nothing focused (right after
    // launch, or once a page swap drops focus) no key arrives at all. Park focus on the page frame
    // then - invisible, it only gives keys somewhere to route from.
    private void EnsureKeyboardFocus()
    {
        if (Content?.XamlRoot is not { } xr || FocusManager.GetFocusedElement(xr) != null) return;
        ContentFrame.IsTabStop = true;
        ContentFrame.UseSystemFocusVisuals = false;
        ContentFrame.Focus(FocusState.Programmatic);
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _shortcuts is not { } s) return;
        var key = e.Key;

        // Settings' recorder: the next real key becomes the binding instead of doing anything.
        if (s.RecordingActionId is { } recordingId)
        {
            if (ShortcutInput.IsModifierKey(key)) return;
            e.Handled = true;
            s.RecordingActionId = null;
            if (key == VirtualKey.Escape) return;   // cancelled - binding unchanged

            var combo = ShortcutInput.Capture(key);
            if (s.FindConflict(combo, recordingId) is { } conflict)
            {
                _ = App.Services.GetRequiredService<IDialogService>().DisplayAlertAsync("Shortcut Already In Use",
                    $"{combo} is already assigned to \"{KeyboardShortcutRegistry.Get(conflict).DisplayName}\". Choose a different combination.", "OK");
                return;
            }
            s.SetCombo(recordingId, combo);
            return;
        }

        bool Is(string id) => ShortcutInput.Matches(s.GetCombo(id), key);

        AppRoute? route =
            Is(KeyboardShortcutIds.GoDashboard) ? AppRoute.Dashboard :
            Is(KeyboardShortcutIds.GoPrBoard) ? AppRoute.Board :
            Is(KeyboardShortcutIds.GoMaterials) && _settings.IsRawPackingTabEnabled ? AppRoute.CallOff :
            Is(KeyboardShortcutIds.GoTasks) ? AppRoute.Tasks :
            Is(KeyboardShortcutIds.GoNotes) ? AppRoute.Notes :
            Is(KeyboardShortcutIds.GoSettings) ? AppRoute.Settings : null;
        if (route is { } r)
        {
            e.Handled = true;
            // An open PR Board dialog closes first rather than being navigated away from.
            if (_current == AppRoute.Board && PrListPageModel.Current?.CloseTopmostModal() == true) return;
            NavigateTo(r, null);
            return;
        }

        if (Is(KeyboardShortcutIds.ToggleSidebar))
        {
            e.Handled = true;
            ToggleSidebar();
            return;
        }

        e.Handled = ContentFrame.Content switch
        {
            PrBoardPage p => p.HandleShortcut(key, s),
            TasksPage p => p.HandleShortcut(key, s),
            NotesPage p => p.HandleShortcut(key, s),
            CallOffPage p => p.HandleShortcut(key, s),
            _ => false,
        };
    }
}
