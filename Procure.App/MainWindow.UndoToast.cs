using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Procure.Services;
using Procure.Utilities;

namespace Procure.App;

// The "Deleted - Undo" toast, and holding the window open until a waiting delete has landed.
public sealed partial class MainWindow
{
    private UndoDeleteService? _undo;
    private DispatcherQueueTimer? _undoTick;
    private DateTime _undoDeadline;
    private bool _undoToastShown;
    private bool _closeAfterDeletes;

    private void InitUndoToast()
    {
        _undo = App.Services.GetRequiredService<UndoDeleteService>();
        _undo.ToastChanged += message =>
        {
            if (DispatcherQueue.HasThreadAccess) OnUndoToastChanged(message);
            else DispatcherQueue.TryEnqueue(() => OnUndoToastChanged(message));
        };

        AppWindow.Closing += OnAppWindowClosing;

        // Lifted off the page with a real shadow. Cast onto the NavigationView (a sibling - a shadow
        // can't fall on its own ancestor); in-tree elements get none without a receiver.
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(Nav);
        UndoToastCard.Shadow = shadow;
        UndoToastCard.Translation = new System.Numerics.Vector3(0, 0, 32);

        ((DoubleAnimation)Anim("UndoToastCountdownAnim").Children[0]).Duration = UndoDeleteService.Window;
        Anim("UndoToastHideAnim").Completed += (_, _) =>
        {
            if (!_undoToastShown) UndoToast.Visibility = Visibility.Collapsed;
        };
    }

    private Storyboard Anim(string key) => (Storyboard)UndoToast.Resources[key];

    private void OnUndoToastChanged(string? message)
    {
        if (message is null)
        {
            HideUndoToast();
            return;
        }

        UndoToastMessage.Text = message;
        _undoDeadline = DateTime.UtcNow + UndoDeleteService.Window;
        UpdateUndoCountdown();

        // A new delete while the toast is up restarts it in place: same card, new text, full bar.
        _undoToastShown = true;
        Anim("UndoToastHideAnim").Stop();
        UndoToast.Visibility = Visibility.Visible;
        Anim("UndoToastShowAnim").Begin();
        var countdown = Anim("UndoToastCountdownAnim");
        countdown.Stop();
        countdown.Begin();

        if (_undoTick is null)
        {
            _undoTick = DispatcherQueue.CreateTimer();
            _undoTick.Interval = TimeSpan.FromMilliseconds(250);
            _undoTick.Tick += (_, _) => UpdateUndoCountdown();
        }
        _undoTick.Start();
    }

    private void HideUndoToast()
    {
        if (!_undoToastShown) return;
        _undoToastShown = false;
        _undoTick?.Stop();
        Anim("UndoToastCountdownAnim").Pause();
        Anim("UndoToastHideAnim").Begin();
    }

    private void UpdateUndoCountdown()
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_undoDeadline - DateTime.UtcNow).TotalSeconds));
        UndoToastCountdown.Text = seconds == 1 ? "You can undo this for 1 second" : $"You can undo this for {seconds} seconds";
    }

    private void UndoToastUndo_Click(object sender, RoutedEventArgs e) => _undo?.Undo();

    private void UndoToastClose_Click(object sender, RoutedEventArgs e) => _ = _undo?.CommitNowAsync();

    /// <summary>Closing the window finishes a delete that is still in its Undo window - the record
    /// was deleted as far as the person closing is concerned, so it must not be there next time.
    /// (If the process is killed instead, the journal finishes it on the next launch.)</summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAfterDeletes || _undo is not { IsBusy: true }) return;

        args.Cancel = true;
        try
        {
            await _undo.CommitNowAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Undo: delete on close failed", ex);
        }
        _closeAfterDeletes = true;
        Close();
    }
}
