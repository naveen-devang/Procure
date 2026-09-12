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
            RefreshThemeState();                       // BoardTheme.IsDark, from the mode string
            _themeSeq++;
            RebindItemColors(ContentFrame.Content as DependencyObject);   // the visible page now
            _reboundSeq[_current] = _themeSeq;
        };
        Activated += OnFirstActivated;
        CompositionTarget.Rendering += OnRendering;

        // Before first render: the pane does not re-theme reliably once it has loaded.
        (_appHost as WinUiAppHost)?.ApplyCurrentTheme();
        if (Content is FrameworkElement c) _shell.WatchOsTheme?.Invoke(c);

        NavigateTo(AppRoute.Board, null);
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
    }

    // The board / tasks / detail colour converters read BoardTheme.IsDark once per container.
    // Resolve it from the chosen mode, NOT from Content.ActualTheme - ActualTheme lags a
    // RequestedTheme change by a layout pass, so reading it here left the tags and status
    // buttons a theme behind until the next board reload ("Refresh").
    private void RefreshThemeState()
    {
        Procure.App.Converters.BoardTheme.IsDark = _settings.AppTheme switch
        {
            "Light" => false,
            "Dark" => true,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
    }

    // The board / tasks / detail colours come from IValueConverters that read BoardTheme.IsDark
    // once, when the item container is realized - they don't re-run on a theme change. Bounce
    // every ItemsControl's source on the visible page so the converters fire again. (Resets
    // scroll position, which is fine for a manual theme toggle.)
    private static void RebindItemColors(DependencyObject? root)
    {
        if (root is null) return;
        if (root is ItemsControl { ItemsSource: { } src } ic)
        {
            ic.ItemsSource = null;
            ic.ItemsSource = src;
        }
        var n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            RebindItemColors(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
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
    private int _themeSeq;
    private readonly Dictionary<AppRoute, int> _reboundSeq = new();

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

            // Pages are singletons: one whose containers were realized in a now-stale theme
            // won't re-run its colour converters on its own. Rebind once when it's shown again.
            if (_reboundSeq.GetValueOrDefault(route, 0) != _themeSeq)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RebindItemColors(ContentFrame.Content as DependencyObject);
                    _reboundSeq[route] = _themeSeq;
                });
            }
        }

        if (route == AppRoute.Board && param == "new" && PrListPageModel.Current is { } b)
            b.ActionParam = "new";

        foreach (var item in EnumerateNavItems())
            if (item.Tag as string == route.ToString()) { Nav.SelectedItem = item; break; }
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
