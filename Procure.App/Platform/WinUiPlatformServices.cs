using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Abstractions;
using Procure.Services;
using Procure.PageModels;

namespace Procure.App.Platform;

// WinUI 3 implementations of the Procure.Core abstractions. See MIGRATION-PLAN.md Phase 2.

public sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue = App.UiQueue;

    public bool IsMainThread => _queue?.HasThreadAccess ?? false;

    public void Post(Action action)
    {
        if (IsMainThread) action();
        else _queue.TryEnqueue(() => action());
    }

    // Every timer that has been started and has not fired yet. Without this nothing holds the timer
    // once PostDelayed returns: the managed wrapper is collectable, the GC takes it, and when Tick
    // fires, t.Stop() calls into a released COM object - an access violation that kills the process
    // outright (0xC0000005 in DispatcherQueueTimer.Stop), with no managed exception and nothing in the
    // crash log. It only happens when a collection lands inside the delay, so it looked random: the
    // app launched cleanly a dozen times and then died on startup, before any input at all.
    private readonly HashSet<DispatcherQueueTimer> _pending = new();
    private readonly object _pendingGate = new();

    public void PostDelayed(TimeSpan delay, Action action)
    {
        var timer = _queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        lock (_pendingGate) _pending.Add(timer);
        timer.Tick += (t, _) =>
        {
            t.Stop();
            lock (_pendingGate) _pending.Remove(t);
            action();
        };
        timer.Start();
    }
}

public sealed class WinUiClipboardService : IClipboardService
{
    public Task<bool> HasTextAsync()
    {
        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        return Task.FromResult(content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text));
    }

    public async Task<string?> GetTextAsync()
    {
        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return null;
        return await content.GetTextAsync();
    }

    public Task SetTextAsync(string text)
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        return Task.CompletedTask;
    }
}

public sealed class WinUiNavigationService : INavigationService
{
    private readonly ShellContext _shell;
    public WinUiNavigationService(ShellContext shell) => _shell = shell;

    /// <summary>Set by MainWindow: (route, param) -> navigate the content frame.</summary>
    public static Action<AppRoute, string?>? Navigate { get; set; }

    public Task GoToAsync(AppRoute route)
    {
        Navigate?.Invoke(route, null);
        return Task.CompletedTask;
    }

    public Task GoToBoardAndCreateAsync()
    {
        Navigate?.Invoke(AppRoute.Board, "new");
        return Task.CompletedTask;
    }

    public Task GoToBoardWithSearchAsync(string search)
    {
        Navigate?.Invoke(AppRoute.Board, null);
        // Several linked record numbers: any one of them should surface, not all at once.
        if (PrListPageModel.Current is { } board) board.SearchAnyOf(search);
        return Task.CompletedTask;
    }
}

public sealed class WinUiDialogService : IDialogService
{
    private readonly ShellContext _shell;
    private readonly ISettingsService _settings;
    public WinUiDialogService(ShellContext shell, ISettingsService settings)
    {
        _shell = shell;
        _settings = settings;
    }

    /// <summary>A ContentDialog is rooted in the XamlRoot's popup root, not under the window
    /// content, so it does not inherit the element-level theme the app switches at runtime -
    /// it followed the application theme, fixed at startup, and stayed in the old theme until
    /// the app was restarted.</summary>
    private ElementTheme Theme => _settings.AppTheme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private async Task<ContentDialogResult> ShowAsync(string title, object content, string? primary, string? secondary, string close)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = close,
            XamlRoot = _shell.XamlRoot,
            RequestedTheme = Theme,
        };
        if (primary is not null) dialog.PrimaryButtonText = primary;
        if (secondary is not null) dialog.SecondaryButtonText = secondary;
        return await dialog.ShowAsync();
    }

    public async Task DisplayAlertAsync(string title, string message, string cancel)
    {
        if (_shell.XamlRoot is null) return;
        await ShowAsync(title, message, null, null, cancel);
    }

    public async Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
    {
        if (_shell.XamlRoot is null) return false;
        var r = await ShowAsync(title, message, accept, null, cancel);
        return r == ContentDialogResult.Primary;
    }

    public async Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons)
    {
        if (_shell.XamlRoot is null) return null;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in buttons) list.Items.Add(b);
        if (destruction is not null) list.Items.Add(destruction);

        string? picked = null;
        list.ItemClick += (_, e) => picked = e.ClickedItem as string;
        list.IsItemClickEnabled = true;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = list,
            CloseButtonText = cancel,
            XamlRoot = _shell.XamlRoot,
            RequestedTheme = Theme,
        };
        list.ItemClick += (_, _) => dialog.Hide();
        await dialog.ShowAsync();
        return picked;
    }

    public async Task<string?> DisplayPromptAsync(string title, string message, string accept, string cancel,
        string? placeholder = null, string initialValue = "")
    {
        if (_shell.XamlRoot is null) return null;
        var box = new TextBox { PlaceholderText = placeholder ?? "", Text = initialValue, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var r = await ShowAsync(title, panel, accept, null, cancel);
        return r == ContentDialogResult.Primary ? box.Text : null;
    }

    public async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_shell.Window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class WinUiAppHost : IAppHost
{
    private readonly ShellContext _shell;
    private readonly ISettingsService _settings;

    public WinUiAppHost(ShellContext shell, ISettingsService settings)
    {
        _shell = shell;
        _settings = settings;
        _shell.ThemeChanged += (_, _) =>
        {
            ApplyAccentColor(_settings.AccentTheme);   // Primary is deep-in-light / pastel-in-dark
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        };
        _settings.SettingsChanged += (_, e) =>
        {
            if (e.Key == nameof(ISettingsService.AccentTheme))
                ApplyAccentColor(_settings.AccentTheme);
        };

        // "System" has to follow Windows while the app is running. Application.RequestedTheme is
        // fixed once the app has launched, so nothing was watching: changing the OS theme did
        // nothing until restart. ActualThemeChanged on the root fires when the OS flips and the
        // root is on ElementTheme.Default, which is exactly the System case.
        _shell.WatchOsTheme = root => root.ActualThemeChanged += (_, _) =>
        {
            if (_settings.AppTheme is "Light" or "Dark") return;
            ApplyAccentColor(_settings.AccentTheme);   // PrimaryTextBrush is deep-in-light / pastel-in-dark
            _shell.RaiseThemeChanged();
        };
    }

    public event EventHandler? ThemeChanged;

    /// <summary>Startup + accent-picker hook. Recolours the accent brushes in place.
    ///
    /// Two rules, both learned the hard way:
    /// 1. RECOLOUR, never replace. Assigning a new SolidColorBrush into the dictionary leaves
    ///    every element that already resolved the key holding the old brush - a ThemeResource
    ///    re-resolves on a theme change, not on a dictionary swap - so the dictionary was right
    ///    and the screen never moved (picking Coral left the pills green).
    /// 2. The framework's own accent keys live in AppColors' THEME dictionaries, and both copies
    ///    are recoloured here. A top-level override loses to generic.xaml's theme dictionary
    ///    during a light/dark re-resolve, which snapped the accent buttons back to the Windows
    ///    system accent the moment you switched mode.</summary>
    public void ApplyAccentColor(string accentId)
    {
        var p = Procure.Models.AccentPalettes.All.FirstOrDefault(
                    x => string.Equals(x.Id, accentId, StringComparison.OrdinalIgnoreCase))
                ?? Procure.Models.AccentPalettes.All[0];

        // From the chosen mode, not ActualTheme: ActualTheme lags a RequestedTheme change by a
        // layout pass, so reading it here picked the shade for the theme we just left.
        var isDark = _settings.AppTheme switch
        {
            "Light" => false,
            "Dark" => true,
            _ => Microsoft.UI.Xaml.Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };

        var light = ParseHex(p.LightHex);   // deep accent, for text/fills on a light ground
        var dark = ParseHex(p.DarkHex);     // pastel accent, same on a dark ground

        // Not theme-scoped (AppColors keeps them top-level), so they follow the current mode.
        Recolour(Microsoft.UI.Xaml.Application.Current.Resources, "AccentFillBrush", dark);
        Recolour(Microsoft.UI.Xaml.Application.Current.Resources, "PrimaryTextBrush", isDark ? dark : light);

        // AccentButtonStyle, checked ToggleButtons, NavigationView selection. Each theme
        // dictionary holds its own brush objects, so a mode switch then needs no re-apply.
        var lightDict = ThemeDict("Light");
        var darkDict = ThemeDict("Default");
        foreach (var (dict, c) in new[] { (lightDict, light), (darkDict, dark) })
        {
            if (dict is null) continue;
            foreach (var key in AccentKeys) Recolour(dict, key, c);
        }
    }

    private static readonly string[] AccentKeys =
    {
        "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
        "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver", "AccentButtonBorderBrushPressed",
        "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver",
        "ToggleButtonBackgroundCheckedPressed", "ToggleButtonBorderBrushChecked",
    };

    /// <summary>AppColors.xaml's Light / Default theme dictionary (it is a merged dictionary,
    /// so the theme dictionaries hang off it, not off Application.Resources).</summary>
    private static Microsoft.UI.Xaml.ResourceDictionary? ThemeDict(string key)
    {
        foreach (var d in Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries)
            // AppBackground identifies AppColors.xaml. Matching on an accent key instead would
            // find XamlControlsResources' own Light/Default dictionaries first (merged earlier),
            // and recolouring those does nothing: ours are merged later and win the lookup.
            if (d.ThemeDictionaries.TryGetValue(key, out var td) && td is Microsoft.UI.Xaml.ResourceDictionary rd
                && rd.ContainsKey("AppBackground"))
                return rd;
        return null;
    }

    /// <summary>Recolour the brush declared under this key, wherever in the merged tree it was
    /// declared. Never inserts: a ResourceDictionary that is already in use rejects new entries
    /// (COMException 0x800F0902), and every key we touch is declared in AppColors.xaml anyway -
    /// a miss means the XAML and this list drifted apart, not that we should add one.</summary>
    private static void Recolour(Microsoft.UI.Xaml.ResourceDictionary res, string key, Windows.UI.Color c)
    {
        if (Find(res, key) is { } b) b.Color = c;   // alpha stays on the brush's Opacity, set in XAML

        static Microsoft.UI.Xaml.Media.SolidColorBrush? Find(Microsoft.UI.Xaml.ResourceDictionary d, string k)
        {
            if (d.TryGetValue(k, out var v) && v is Microsoft.UI.Xaml.Media.SolidColorBrush hit) return hit;
            foreach (var m in d.MergedDictionaries)
                if (Find(m, k) is { } deeper) return deeper;
            return null;
        }
    }

    private static Windows.UI.Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 255, r, g, b;
        if (hex.Length == 8)
        {
            a = Convert.ToByte(hex.Substring(0, 2), 16);
            hex = hex.Substring(2);
        }
        r = Convert.ToByte(hex.Substring(0, 2), 16);
        g = Convert.ToByte(hex.Substring(2, 2), 16);
        b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }

    public void Quit() => Microsoft.UI.Xaml.Application.Current.Exit();

    public Task OpenFileAsync(string path) =>
        Windows.System.Launcher.LaunchUriAsync(new Uri(path)).AsTask();

    /// <summary>The one place that turns a mode string into an applied theme. MainWindow used to
    /// do it as well, from its own copy of the switch; two owners meant a change could land twice
    /// or, at startup, three times.</summary>
    public Task ApplyThemeAsync(string mode)
    {
        _settings.AppTheme = mode;
        ApplyCurrentTheme();
        _shell.RaiseThemeChanged();
        return Task.CompletedTask;
    }

    /// <summary>Pushes the saved mode onto the live tree. Also the startup path, so the theme is
    /// right before first render (NavigationView does not re-theme its pane reliably once it has
    /// loaded, which left a light-mode tab bar dark).</summary>
    public void ApplyCurrentTheme()
    {
        if (_shell.Window?.Content is not FrameworkElement root) return;
        var t = CurrentTheme;
        if (root.RequestedTheme != t) root.RequestedTheme = t;
        _shell.ApplyThemeToNav?.Invoke(t);
    }

    public ElementTheme CurrentTheme => _settings.AppTheme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
