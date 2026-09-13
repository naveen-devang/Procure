using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        ThemeSwitchExpanded.Visibility = Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        CompactThemeBtn.Visibility = Nav.IsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AutoCollapseSidebar(double width)
    {
        if (!_settings.AutoCollapseSidebarOnNarrow || _sidebarToggledByHand || width <= 0) return;
        var narrow = width < AppConstants.ResponsiveCollapseBreakpoint;
        if (_settings.IsSidebarCompact != narrow) _settings.IsSidebarCompact = narrow;
    }

    // ---- Light / Dark switch ----
    // Same path as Settings > Color Mode, so both stay in step (SettingsChanged updates the other).

    private void SetTheme(string mode) => _ = App.Services.GetRequiredService<IAppHost>().ApplyThemeAsync(mode);
    private void LightMode_Click(object sender, RoutedEventArgs e) => SetTheme("Light");
    private void DarkMode_Click(object sender, RoutedEventArgs e) => SetTheme("Dark");
    private void CompactTheme_Click(object sender, RoutedEventArgs e) => SetTheme(Converters.BoardTheme.IsDark ? "Light" : "Dark");

    /// <summary>The current theme's button at full strength and the other dimmed (MAUI's look); the collapsed
    /// toggle shows the current theme's icon. "System" follows whatever Windows is showing.</summary>
    private void PaintThemeSwitch(bool dark)
    {
        LightModeBtn.Opacity = dark ? 0.4 : 1.0;
        DarkModeBtn.Opacity = dark ? 1.0 : 0.4;
        CompactThemeBtn.Content = dark ? "" : "";   // moon / sun
        ToolTipService.SetToolTip(CompactThemeBtn, dark ? "Switch to Light Theme" : "Switch to Dark Theme");
    }

    private void ApplyRawPackingTab()
    {
        var enabled = _settings.IsRawPackingTabEnabled;
        RawPackingNavItem.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        // Turned off while it is the page on screen: don't strand the user on a tab that just vanished.
        if (!enabled && _current == AppRoute.CallOff) NavigateTo(AppRoute.Dashboard, null);
    }
}
