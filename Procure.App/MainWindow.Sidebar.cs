using Microsoft.UI.Xaml;
using Procure.Abstractions;
using Procure.Services;

namespace Procure.App;

// The Settings > Navigation & Sidebar switches, as the MAUI AppShell applied them:
//   Compact sidebar     - the pane collapses to icons; the title-bar button and Ctrl+B flip the saved setting.
//   Auto-collapse       - below 1024 wide the sidebar goes compact, above it expands, until the user
//                         toggles it by hand this session.
//   Raw & Packing tab   - shows or hides that sidebar entry.
public sealed partial class MainWindow
{
    private bool _sidebarToggledByHand;

    private void InitSidebar()
    {
        ApplySidebarCompact();
        ApplyRawPackingTab();
        SizeChanged += (_, e) => AutoCollapseSidebar(e.Size.Width);
        _settings.SettingsChanged += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.Key)
            {
                case nameof(ISettingsService.IsSidebarCompact): ApplySidebarCompact(); break;
                case nameof(ISettingsService.IsRawPackingTabEnabled): ApplyRawPackingTab(); break;
            }
        });
    }

    private void ToggleSidebar()
    {
        _sidebarToggledByHand = true;
        _settings.IsSidebarCompact = !_settings.IsSidebarCompact;
    }

    private void ApplySidebarCompact()
    {
        Nav.IsPaneOpen = !_settings.IsSidebarCompact;
        RefreshUpdateReadyVisibility();   // the update card only fits beside an open sidebar
    }

    private void AutoCollapseSidebar(double width)
    {
        if (!_settings.AutoCollapseSidebarOnNarrow || _sidebarToggledByHand || width <= 0) return;
        var narrow = width < AppConstants.ResponsiveCollapseBreakpoint;
        if (_settings.IsSidebarCompact != narrow) _settings.IsSidebarCompact = narrow;
    }

    private void ApplyRawPackingTab()
    {
        var enabled = _settings.IsRawPackingTabEnabled;
        RawPackingNavItem.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        // Turned off while it is the page on screen: don't strand the user on a tab that just vanished.
        if (!enabled && _current == AppRoute.CallOff) NavigateTo(AppRoute.Dashboard, null);
    }
}
