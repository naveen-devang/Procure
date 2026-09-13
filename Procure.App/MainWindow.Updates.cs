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

    // Already downloaded; Velopack applies it and relaunches, exiting this process on success.
    private void UpdateRestart_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Services.GetRequiredService<IUpdateService>().LaunchInstaller(string.Empty))
            UpdateLater_Click(sender, e);
    }
}
