using System;
using System.Threading.Tasks;

namespace Procure.Abstractions;

// Platform services for self-checks and tests that drive a view model without a UI
// host. Post runs inline; PostDelayed keeps a real timer so debounce timing still
// holds (a self-check that sets a value, waits, then asserts sees the same behaviour
// it saw against the MAUI dispatcher).

public sealed class HeadlessUiDispatcher : IUiDispatcher
{
    public bool IsMainThread => true;
    public void Post(Action action) => action();
    public void PostDelayed(TimeSpan delay, Action action) =>
        _ = Task.Delay(delay).ContinueWith(_ => action(), TaskScheduler.Default);
}

public sealed class HeadlessDialogService : IDialogService
{
    public Task DisplayAlertAsync(string title, string message, string cancel) => Task.CompletedTask;
    public Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) => Task.FromResult(true);
    public Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons) => Task.FromResult<string?>(null);
    public Task<string?> DisplayPromptAsync(string title, string message, string accept, string cancel, string? placeholder = null, string initialValue = "") => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
}

public sealed class HeadlessNavigationService : INavigationService
{
    public Task GoToAsync(AppRoute route) => Task.CompletedTask;
    public Task GoToBoardAndCreateAsync() => Task.CompletedTask;
    public Task GoToBoardWithSearchAsync(string search) => Task.CompletedTask;
}
