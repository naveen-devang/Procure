using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Procure.Abstractions;
using Procure.Services;
using Procure.Utilities;

namespace Procure.App;

// Background updates, as in the MAUI app (App.xaml.cs / AppShell there): no prompt, no popup. Checks at
// launch and then at least every 24h while open; a found update downloads immediately, and the only thing
// anyone sees is the sidebar's "Update ready" card once there is something to restart into.
public sealed partial class MainWindow
{
    private bool _updateReady;

    private void StartUpdateWork()
    {
        _ = CheckForUpdatesInBackgroundAsync();
        _ = ShowWhatsNewIfJustUpdatedAsync();
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        await Task.Delay(3000);   // let the first screen settle
        var isFirstCheck = true;
        var updates = App.Services.GetRequiredService<IUpdateService>();

        while (true)
        {
            try
            {
                // Read each cycle, so turning the setting off in Settings stops the daily checks too.
                if (_settings.AutoCheckUpdatesOnStartup
                    && (isFirstCheck || UpdateCheckScheduler.ShouldCheckNow(UpdateStateStore.GetLastUpdateCheckUtc(), DateTime.UtcNow)))
                {
                    var update = await updates.CheckForUpdatesAsync(AppConstants.GitHubRepository);
                    UpdateStateStore.SetLastUpdateCheckUtc(DateTime.UtcNow);
                    if (update.IsUpdateAvailable)
                    {
                        // UpdateService dedupes this against a Settings "Download & Install" click.
                        await updates.DownloadUpdateAsync(update);
                        if (updates.DownloadStatus == UpdateDownloadStatus.Done)
                            DispatcherQueue.TryEnqueue(() => ShowUpdateReady(update.TagName));
                    }
                }
            }
            catch
            {
                // Silent: the next cycle tries again, and nobody asked to see a failed check.
            }
            isFirstCheck = false;
            // Woken hourly; ShouldCheckNow holds it to one real check a day (survives sleep/resume better
            // than a single 24h delay).
            await Task.Delay(TimeSpan.FromHours(1));
        }
    }

    private async Task ShowWhatsNewIfJustUpdatedAsync()
    {
        try
        {
            var updates = App.Services.GetRequiredService<IUpdateService>();
            var current = updates.CurrentVersionString;

            // A dev build reports "Dev build" as its version, which never matches what was last
            // shown - so every F5 popped a "What's New in vDev build" box over the app.
            if (current == "Dev build") return;

            var lastShown = UpdateStateStore.GetLastWhatsNewVersionShown();
            if (string.IsNullOrEmpty(lastShown))
            {
                // First run with the marker: nothing to announce, start tracking from here.
                UpdateStateStore.SetLastWhatsNewVersionShown(current);
                return;
            }
            if (lastShown == current) return;

            // Written before the network call, so a slow or failed fetch can't make it show every launch.
            UpdateStateStore.SetLastWhatsNewVersionShown(current);
            var notes = await updates.GetReleaseNotesForVersionAsync(AppConstants.GitHubRepository, current);

            await Task.Delay(1500);   // the dialog needs the window's content to be up
            var message = string.IsNullOrWhiteSpace(notes) ? "Procure has been updated. Check Settings for release details." : notes;
            DispatcherQueue.TryEnqueue(() => _ = App.Services.GetRequiredService<IDialogService>()
                .DisplayAlertAsync($"What's New in v{current}", message, "OK"));
        }
        catch
        {
            // A nice-to-have; never worth disturbing startup.
        }
    }

    private void ShowUpdateReady(string versionTag)
    {
        _updateReady = true;
        UpdateReadyText.Text = string.IsNullOrWhiteSpace(versionTag)
            ? "Downloaded in the background. Restart to apply."
            : $"{versionTag} downloaded in the background. Restart to apply.";
        RefreshUpdateReadyVisibility();
    }

    // Card when the sidebar is open, dot on Settings when it is collapsed to icons.
    private void RefreshUpdateReadyVisibility()
    {
        UpdateReadyCard.Visibility = _updateReady && Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        UpdateReadyDot.Visibility = _updateReady && !Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        _updateReady = false;
        RefreshUpdateReadyVisibility();
    }

    // The sidebar card's "Restart" no longer quits on the spot: it opens the update window, which
    // says what is about to happen before anything closes.
    private void UpdateRestart_Click(object sender, RoutedEventArgs e) => ShowUpdateDialog();

    /// <summary>Opens the update window. Safe to call whether the download has finished or is still
    /// running - it reads the shared update state rather than keeping its own.</summary>
    public void ShowUpdateDialog()
    {
        var updates = App.Services.GetRequiredService<IUpdateService>();
        RefreshUpdateDialog(updates);

        if (!_updateDialogHooked)
        {
            updates.UpdateStateChanged += OnUpdateServiceStateChanged;
            _updateDialogHooked = true;
        }

        UpdateOverlay.Visibility = Visibility.Visible;
    }

    private bool _updateDialogHooked;
    private bool _updateInstalling;

    private void OnUpdateServiceStateChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (UpdateOverlay.Visibility == Visibility.Visible)
                RefreshUpdateDialog(App.Services.GetRequiredService<IUpdateService>());
        });

    private void RefreshUpdateDialog(IUpdateService updates)
    {
        if (_updateInstalling) return;   // the closing message must not be overwritten mid-handover

        var tag = updates.PendingUpdateTag ?? updates.LastKnownUpdate?.TagName;
        UpdateDialogTitle.Text = string.IsNullOrWhiteSpace(tag) ? "Update ready" : $"Update ready - {tag}";

        var downloading = updates.DownloadStatus == UpdateDownloadStatus.Running;
        UpdateDialogProgress.IsIndeterminate = false;
        UpdateDialogProgress.Value = downloading ? updates.DownloadProgress * 100 : 100;

        UpdateDialogStatus.Text = updates.DownloadStatus switch
        {
            UpdateDownloadStatus.Running => $"Downloading… {updates.DownloadProgress * 100:F0}%",
            UpdateDownloadStatus.Failed => "The download did not finish. Try again from Settings.",
            _ => "Downloaded and waiting to be installed."
        };

        UpdateDialogHint.Text = "The app will close and reopen by itself.";
        UpdateDialogInstall.IsEnabled = updates.DownloadStatus == UpdateDownloadStatus.Done;
        UpdateDialogLater.IsEnabled = true;
    }

    /// <summary>The same window with stand-in text, for looking at it on a dev build where no real
    /// update exists (PROCURE_UPDATE_UI=1). Installing does nothing but show the closing message.</summary>
    private void ShowUpdateDialogPreview()
    {
        _updatePreview = true;
        _updateInstalling = true;   // keeps the live refresh from overwriting the stand-in text
        UpdateDialogTitle.Text = "Update ready - v2.0.9";
        UpdateDialogStatus.Text = "Downloaded and waiting to be installed.";
        UpdateDialogHint.Text = "The app will close and reopen by itself.";
        UpdateDialogProgress.IsIndeterminate = false;
        UpdateDialogProgress.Value = 100;
        UpdateDialogInstall.IsEnabled = true;
        UpdateDialogLater.IsEnabled = true;
        UpdateOverlay.Visibility = Visibility.Visible;
    }

    private bool _updatePreview;

    private void UpdateDialogLater_Click(object sender, RoutedEventArgs e)
    {
        UpdateOverlay.Visibility = Visibility.Collapsed;
        _updateInstalling = false;
        _updatePreview = false;
    }

    // Already downloaded; Velopack applies it and relaunches, exiting this process on success. The
    // message is set BEFORE the call, which is the only moment left to explain the app vanishing.
    private void UpdateDialogInstall_Click(object sender, RoutedEventArgs e)
    {
        _updateInstalling = true;
        UpdateDialogStatus.Text = "Installing the update…";
        UpdateDialogHint.Text = "This window will close and the app will reopen on its own.";
        UpdateDialogProgress.IsIndeterminate = true;
        UpdateDialogInstall.IsEnabled = false;
        UpdateDialogLater.IsEnabled = false;

        // Give the frame a chance to paint the message above before the process is taken down.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_updatePreview)
            {
                UpdateDialogHint.Text = "Preview only - nothing is being installed.";
                return;
            }

            if (App.Services.GetRequiredService<IUpdateService>().LaunchInstaller(string.Empty)) return;

            _updateInstalling = false;
            UpdateDialogStatus.Text = "The update could not be installed. Try again from Settings.";
            UpdateDialogHint.Text = "Your work is untouched.";
            UpdateDialogProgress.IsIndeterminate = false;
            UpdateDialogLater.IsEnabled = true;
            UpdateDialogInstall.IsEnabled = true;
        });
    }
}
