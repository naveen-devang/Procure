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

        Activated += OnFirstActivated;
        CompositionTarget.Rendering += OnRendering;
        NavigateTo(AppRoute.Board, null);
    }

    private bool _themeApplied;
    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_themeApplied) return;
        _themeApplied = true;
        _ = _appHost.ApplyThemeAsync(_settings.AppTheme);
        (_appHost as WinUiAppHost)?.ApplyAccentColor(_settings.AccentTheme);
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
                _ => new StubPage(route.ToString()),
            };
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
                HudText.Text = $"frame {_emaMs:F1} ms ({1000.0 / Math.Max(_emaMs, 0.01):F0} fps)   worst/1s {_worstMs:F1} ms{load}";
                _worstMs = 0;
                _windowStart = DateTime.UtcNow;
            }
        }
        _lastTicks = now;
    }
}
