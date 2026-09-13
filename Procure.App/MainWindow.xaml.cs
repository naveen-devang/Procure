using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Procure.Abstractions;
using Procure.Services;
using Procure.App.Platform;
using Procure.App.Views;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.App;

public sealed partial class MainWindow : Window
{
    private readonly ShellContext _shell;
    private readonly ISettingsService _settings;
    private readonly IAppHost _appHost;

    private readonly System.Diagnostics.Process _proc = System.Diagnostics.Process.GetCurrentProcess();
    private long _lastTicks;
    private double _emaMs;
    private double _worstMs;
    private DateTime _windowStart = DateTime.UtcNow;
    private string _lastLoadWhat = "";
    private long _lastLoadMs;

    public MainWindow(ShellContext shell, ISettingsService settings, IAppHost appHost)
    {
        InitializeComponent();
        _shell = shell;
        _settings = settings;
        _appHost = appHost;

        ExtendsContentIntoTitleBar = true;
        // Drag area and caption-button reservation - see AppTitleBar in MainWindow.xaml. Tall (48) so
        // the bar lines up with the sidebar's own 48 px items.
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        _shell.Window = this;
        _shell.ContentFrame = ContentFrame;

        WinUiNavigationService.Navigate = (route, param) =>
            DispatcherQueue.TryEnqueue(() => NavigateTo(route, param));

        PerfProbe.PageLoad = (what, ms) => DispatcherQueue.TryEnqueue(() =>
        {
            _lastLoadWhat = what;
            _lastLoadMs = ms;
        });

        _shell.ApplyThemeToNav = t => { if (Nav.RequestedTheme != t) Nav.RequestedTheme = t; };

        _shell.ThemeChanged += (_, _) =>
        {
            RefreshThemeState();   // BoardTheme.IsDark, from the mode string - recolours the live converter brushes
        };
        Activated += OnFirstActivated;

        // The frame/RAM overlay is a development instrument, not a feature: it is a green box over
        // the corner of someone's procurement app, and its Rendering handler makes the UI thread
        // compose a frame continuously whether or not anything changed. Opt in with PROCURE_HUD=1.
        if (Environment.GetEnvironmentVariable("PROCURE_HUD") == "1")
        {
            Hud.Visibility = Visibility.Visible;
            CompositionTarget.Rendering += OnRendering;
        }

        // Before first render: the pane does not re-theme reliably once it has loaded.
        (_appHost as WinUiAppHost)?.ApplyCurrentTheme();
        RefreshThemeState();   // caption button colours right from the first frame, not after activation
        if (Content is FrameworkElement c) _shell.WatchOsTheme?.Invoke(c);

        InstallShortcuts();
        NavigateTo(AppRoute.Dashboard, null);   // the page the app opens on, as in the MAUI app
    }

    private bool _themeApplied;
    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_themeApplied) return;
        _themeApplied = true;
        // The constructor already applied the theme and App.OnLaunched the accent; this only has
        // to settle BoardTheme.IsDark. It used to re-run ApplyThemeAsync, which wrote the setting
        // back and raised a change - a third apply, and a full rebind, before the first frame.
        RefreshThemeState();
        Procure.App.Platform.PopupCursorFix.Install(DispatcherQueue);   // no busy cursor over dropdowns
        EnsureKeyboardFocus();
    }

    // The board / tasks / detail colour converters hand out brushes that follow BoardTheme.IsDark.
    // Resolve it from the chosen mode, NOT from Content.ActualTheme - ActualTheme lags a
    // RequestedTheme change by a layout pass, so reading it here left the tags and status
    // buttons a theme behind until the next board reload ("Refresh").
    private void RefreshThemeState()
    {
        Procure.App.Converters.BoardTheme.IsDark = _settings.AppTheme switch
        {
            "Light" => false,
            "Dark" => true,
            // System: the root sits on ElementTheme.Default, so its ActualTheme is the OS theme and is
            // already settled when WatchOsTheme raises this. Application.RequestedTheme is frozen at launch.
            _ => (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark,
        };
        ApplyCaptionButtonColors(Procure.App.Converters.BoardTheme.IsDark);
    }

    // The sidebar toggle lives in the title bar now; it opens and collapses the pane exactly as the
    // pane's own button did.
    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    /// <summary>
    /// Minimise / maximise / close. Windows draws these itself and does not follow the app's theme:
    /// it uses whatever colours it is handed, so without this their symbols and hover fill stayed in
    /// Windows' defaults - faint on one theme, and unchanged after switching Light/Dark. Windows only
    /// allows transparency on the resting background, so hover and pressed use the app's solid fills
    /// (AppSubtleFill / a step deeper). Close keeps Windows' own red hover.
    /// </summary>
    private void ApplyCaptionButtonColors(bool dark)
    {
        var bar = AppWindow.TitleBar;
        static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        if (dark)
        {
            bar.ButtonForegroundColor = C(0xF1, 0xEF, 0xEC);          // AppTextPrimary
            bar.ButtonInactiveForegroundColor = C(0x6F, 0x6A, 0x62);  // AppTextTertiary
            bar.ButtonHoverBackgroundColor = C(0x2A, 0x2A, 0x30);     // AppSubtleFill
            bar.ButtonHoverForegroundColor = C(0xF1, 0xEF, 0xEC);
            bar.ButtonPressedBackgroundColor = C(0x33, 0x33, 0x3A);
            bar.ButtonPressedForegroundColor = C(0xF1, 0xEF, 0xEC);
        }
        else
        {
            bar.ButtonForegroundColor = C(0x37, 0x33, 0x2E);
            bar.ButtonInactiveForegroundColor = C(0xA3, 0x9C, 0x92);
            bar.ButtonHoverBackgroundColor = C(0xF0, 0xED, 0xE6);
            bar.ButtonHoverForegroundColor = C(0x37, 0x33, 0x2E);
            bar.ButtonPressedBackgroundColor = C(0xE6, 0xE2, 0xD9);
            bar.ButtonPressedForegroundColor = C(0x37, 0x33, 0x2E);
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem { Tag: string tag }
            && Enum.TryParse<AppRoute>(tag, out var route))
        {
            NavigateTo(route, null);
        }
    }

    private AppRoute _current = (AppRoute)(-1);

    private void NavigateTo(AppRoute route, string? param)
    {
        if (route != _current)
        {
            _current = route;
            ContentFrame.Content = route switch
            {
                AppRoute.Board => App.Services.GetService(typeof(PrBoardPage)) ?? new PrBoardPage(),
                AppRoute.Dashboard => App.Services.GetService(typeof(DashboardPage))!,
                AppRoute.Tasks => App.Services.GetService(typeof(TasksPage))!,
                AppRoute.Notes => App.Services.GetService(typeof(NotesPage))!,
                AppRoute.CallOff => App.Services.GetService(typeof(CallOffPage))!,
                AppRoute.Settings => App.Services.GetService(typeof(SettingsPage))!,
                _ => new StubPage(route.ToString()),
            };
        }

        if (route == AppRoute.Board && param == "new" && PrListPageModel.Current is { } b)
            b.ActionParam = "new";

        // Several linked record numbers: any one of them should surface, not all at once.
        if (route == AppRoute.Board && param?.StartsWith(WinUiNavigationService.SearchParamPrefix, StringComparison.Ordinal) == true
            && PrListPageModel.Current is { } board)
            board.SearchAnyOf(param[WinUiNavigationService.SearchParamPrefix.Length..]);

        foreach (var item in EnumerateNavItems())
            if (item.Tag as string == route.ToString()) { Nav.SelectedItem = item; break; }

        // A page swap can leave nothing focused, which would silence every shortcut until a click.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, EnsureKeyboardFocus);
    }

    private IEnumerable<NavigationViewItem> EnumerateNavItems()
    {
        foreach (var o in Nav.MenuItems) if (o is NavigationViewItem i) yield return i;
        foreach (var o in Nav.FooterMenuItems) if (o is NavigationViewItem i) yield return i;
    }

    private void OnRendering(object? sender, object e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastTicks != 0)
        {
            var ms = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;
            _emaMs = _emaMs == 0 ? ms : _emaMs * 0.9 + ms * 0.1;
            if (ms > _worstMs) _worstMs = ms;

            if ((DateTime.UtcNow - _windowStart).TotalSeconds >= 1)
            {
                var load = _lastLoadMs > 0 ? $"   load {_lastLoadMs} ms ({_lastLoadWhat})" : "";
                _proc.Refresh();
                var ws = _proc.WorkingSet64 / (1024.0 * 1024.0);
                var gc = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                HudText.Text = $"frame {_emaMs:F1} ms ({1000.0 / Math.Max(_emaMs, 0.01):F0} fps)   worst/1s {_worstMs:F1} ms   ram {ws:F0} MB (gc {gc:F0}){load}";
                _worstMs = 0;
                _windowStart = DateTime.UtcNow;
            }
        }
        _lastTicks = now;
    }
}
