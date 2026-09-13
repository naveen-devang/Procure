using System;

namespace Procure.Abstractions;

/// <summary>
/// Marshals work onto the UI thread. Replaces
/// <c>MainThread.BeginInvokeOnMainThread</c> and
/// <c>Dispatcher.GetForCurrentThread().Dispatch / DispatchDelayed</c>.
/// MAUI host: the app dispatcher. WinUI host: <c>DispatcherQueue</c>.
/// </summary>
public interface IUiDispatcher
{
    bool IsMainThread { get; }

    /// <summary>Queue <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);

    /// <summary>Run <paramref name="action"/> on the UI thread after <paramref name="delay"/>.</summary>
    void PostDelayed(TimeSpan delay, Action action);
}
