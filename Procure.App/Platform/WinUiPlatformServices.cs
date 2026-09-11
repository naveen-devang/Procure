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

    public void PostDelayed(TimeSpan delay, Action action)
    {
        var timer = _queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (t, _) => { t.Stop(); action(); };
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
        if (PrListPageModel.Current is { } board) board.SearchText = search;
        return Task.CompletedTask;
    }
}

public sealed class WinUiDialogService : IDialogService
{
    private readonly ShellContext _shell;
    public WinUiDialogService(ShellContext shell) => _shell = shell;

    private async Task<ContentDialogResult> ShowAsync(string title, object content, string? primary, string? secondary, string close)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = close,
            XamlRoot = _shell.XamlRoot,
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
    }

    public event EventHandler? ThemeChanged;

    /// <summary>Startup + accent-picker hook. Rewrites AccentFillBrush / PrimaryTextBrush
    /// (App.xaml keeps them out of the theme dictionaries for exactly this).</summary>
    public void ApplyAccentColor(string accentId)
    {
        var p = Procure.Models.AccentPalettes.All.FirstOrDefault(
                    x => string.Equals(x.Id, accentId, StringComparison.OrdinalIgnoreCase))
                ?? Procure.Models.AccentPalettes.All[0];

        var isDark = _shell.Window?.Content is FrameworkElement fe
                     && fe.ActualTheme == ElementTheme.Dark;

        var fill = ParseHex(p.DarkHex);                       // pastel fill, both modes
        var primary = ParseHex(isDark ? p.DarkHex : p.LightHex);   // text/icons on plain bg

        var res = Microsoft.UI.Xaml.Application.Current.Resources;
        res["AccentFillBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(fill);
        res["PrimaryTextBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(primary);

        // Repoint the Fluent accent brushes so AccentButtonStyle, ToggleButton-checked,
        // NavigationView selection etc. use the app's pastel accent instead of the bright
        // Windows system accent. 'primary' pairs with TextOnAccentFillColorPrimaryBrush
        // (white in light, near-black in dark) - deep accent in light, pastel in dark.
        Microsoft.UI.Xaml.Media.SolidColorBrush B(Windows.UI.Color c) => new(c);
        res["AccentFillColorDefaultBrush"] = B(primary);
        res["AccentFillColorSecondaryBrush"] = B(WithAlpha(primary, 0.90));
        res["AccentFillColorTertiaryBrush"] = B(WithAlpha(primary, 0.80));
        // AccentButtonStyle resolves these at style-load from generic.xaml, so the
        // AccentFillColor* swap above doesn't reach it - set them directly too.
        res["AccentButtonBackground"] = B(primary);
        res["AccentButtonBackgroundPointerOver"] = B(WithAlpha(primary, 0.90));
        res["AccentButtonBackgroundPressed"] = B(WithAlpha(primary, 0.80));
        res["AccentButtonBorderBrush"] = B(primary);
        res["AccentButtonBorderBrushPointerOver"] = B(WithAlpha(primary, 0.90));
        res["AccentButtonBorderBrushPressed"] = B(WithAlpha(primary, 0.80));

        // Same story for ToggleButton's checked state (filter chips, Settings' Color Mode
        // pills, theme toggles) - resolved at style-load too, same fix.
        res["ToggleButtonBackgroundChecked"] = B(primary);
        res["ToggleButtonBackgroundCheckedPointerOver"] = B(WithAlpha(primary, 0.90));
        res["ToggleButtonBackgroundCheckedPressed"] = B(WithAlpha(primary, 0.80));
        res["ToggleButtonBorderBrushChecked"] = B(primary);
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color c, double a) =>
        Windows.UI.Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);

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

    public Task ApplyThemeAsync(string mode)
    {
        _settings.AppTheme = mode;
        if (_shell.Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = mode switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        _shell.RaiseThemeChanged();
        return Task.CompletedTask;
    }
}
