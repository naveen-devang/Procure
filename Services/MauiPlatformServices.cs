using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Procure.Abstractions;

namespace Procure.Services;

// MAUI-side implementations of the Procure.Core abstractions. These vanish when the
// WinUI host replaces them; the view models only ever see the interfaces.
// See MIGRATION-PLAN.md Phase 1b.

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public bool IsMainThread => MainThread.IsMainThread;

    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);

    public void PostDelayed(TimeSpan delay, Action action) =>
        MainThread.BeginInvokeOnMainThread(() =>
            Application.Current?.Dispatcher.DispatchDelayed(delay, action));
}

public sealed class MauiNavigationService : INavigationService
{
    private static string Route(AppRoute route) => route switch
    {
        AppRoute.Dashboard => "//main",
        AppRoute.Board => "//prboard",
        AppRoute.Tasks => "//todos",
        AppRoute.Notes => "//notes",
        AppRoute.CallOff => "//calloff",
        AppRoute.Settings => "//settings",
        _ => "//main",
    };

    public Task GoToAsync(AppRoute route) =>
        Shell.Current is null ? Task.CompletedTask : Shell.Current.GoToAsync(Route(route));

    public Task GoToBoardAndCreateAsync() =>
        Shell.Current is null ? Task.CompletedTask : Shell.Current.GoToAsync("//prboard?action=new");
}

public sealed class MauiDialogService : IDialogService
{
    private static Page? Root =>
        Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;

    public Task DisplayAlertAsync(string title, string message, string cancel) =>
        Root?.DisplayAlertAsync(title, message, cancel) ?? Task.CompletedTask;

    public Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) =>
        Root?.DisplayAlertAsync(title, message, accept, cancel) ?? Task.FromResult(false);

    public async Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons) =>
        Root is null ? null : await Root.DisplayActionSheetAsync(title, cancel, destruction, buttons);
}

public sealed class MauiClipboardService : IClipboardService
{
    public Task<bool> HasTextAsync() => Task.FromResult(Clipboard.Default.HasText);
    public Task<string?> GetTextAsync() => Clipboard.Default.GetTextAsync();
    public Task SetTextAsync(string text) => Clipboard.Default.SetTextAsync(text);
}

public sealed class MauiAppHost : IAppHost
{
    public event EventHandler? ThemeChanged;

    public MauiAppHost()
    {
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged += (_, _) => ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Quit() => Application.Current?.Quit();

    public Task OpenFileAsync(string path) =>
        Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(path) });
}
