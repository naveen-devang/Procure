using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Procure.Services;

namespace Procure
{
    public partial class App : Application
    {
        private readonly IServiceProvider _services;

        public App(IServiceProvider services)
        {
            InitializeComponent();
            _services = services;

            // OS theme flips must drop ThemeHelper's cached value. App lives for the process lifetime.
            RequestedThemeChanged += OnRequestedThemeChanged;

            // Apply persisted theme mode and pastel accent color
            var settings = _services.GetRequiredService<ISettingsService>();
            settings.ApplySavedTheme();

            // Background update check if enabled
            if (settings.AutoCheckUpdatesOnStartup)
            {
                _ = CheckForUpdatesInBackgroundAsync();
            }

            // One-time "What's New" prompt right after an update lands and relaunches the app -
            // independent of the check above, which only ever looks at *future* releases.
            _ = ShowWhatsNewIfJustUpdatedAsync();

#if DEBUG
            // Opt-in only: PROCURE_SELFCHECK=1. One environment read on a Debug launch, nothing in Release.
            if (Environment.GetEnvironmentVariable("PROCURE_SELFCHECK") == "1")
            {
                _ = Data.DatabaseSelfCheck.RunAsync(
                    _services.GetRequiredService<Data.SqliteDatabase>(),
                    _services.GetRequiredService<Data.Repositories.IPurchaseRequisitionRepository>());
            }

            // Opt-in only: PROCURE_UPDATE_SELFCHECK=1.
            if (Environment.GetEnvironmentVariable("PROCURE_UPDATE_SELFCHECK") == "1")
            {
                Utilities.UpdateCheckSchedulerSelfCheck.Run();
                Utilities.UpdateStateStoreSelfCheck.Run();
            }

            // Opt-in only: PROCURE_TODO_SELFCHECK=1.
            if (Environment.GetEnvironmentVariable("PROCURE_TODO_SELFCHECK") == "1")
            {
                _ = Data.TodoRepositorySelfCheck.RunAsync(
                    _services.GetRequiredService<Data.Repositories.ITodoRepository>());
            }
#endif
        }

        private static void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            Procure.Utilities.ThemeHelper.Invalidate();
#if WINDOWS
            // Windows flipped (System mode) or the app did: hidden pages' native trees must follow.
            if (Shell.Current is AppShell shell) shell.ApplyNativeThemeToPages();
#endif
        }

        // No permission asked, no popup: checks every launch, then at least once every 24h for
        // as long as the app stays open. The persisted lastCheck gate (UpdateStateStore, across
        // launches) only throttles the *recurring* loop below - it used to also gate the launch
        // check itself, which meant restarting within the same 24h window (e.g. to pick up a
        // release that just went out) silently skipped checking at all. Finding an update
        // downloads it immediately in the background: the only thing the user ever sees is
        // AppShell's sidebar card, and only once the download is actually done and there's
        // something to restart into.
        private async Task CheckForUpdatesInBackgroundAsync()
        {
            await Task.Delay(3000); // Allow UI to initialize first

            var isFirstCheck = true;

            while (true)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(AppConstants.GitHubRepository))
                    {
                        var lastCheck = Utilities.UpdateStateStore.GetLastUpdateCheckUtc();
                        if (isFirstCheck || Utilities.UpdateCheckScheduler.ShouldCheckNow(lastCheck, DateTime.UtcNow))
                        {
                            var updateService = _services.GetRequiredService<IUpdateService>();
                            var update = await updateService.CheckForUpdatesAsync(AppConstants.GitHubRepository);
                            Utilities.UpdateStateStore.SetLastUpdateCheckUtc(DateTime.UtcNow);

                            if (update.IsUpdateAvailable)
                            {
                                await updateService.DownloadUpdateAsync(update);
                                NotifyUpdateReady(update.TagName);
                            }
                        }
                    }
                }
                catch
                {
                    // Silently ignore - the next cycle tries again, and a failed check/download
                    // must never be visible to someone who was never asked to look at this.
                }

                isFirstCheck = false;
                await Task.Delay(Utilities.UpdateCheckScheduler.MinimumInterval);
            }
        }

        private void NotifyUpdateReady(string versionTag)
        {
            if (Current?.Windows.Count > 0 && Current.Windows[0].Page is AppShell shell)
            {
                shell.ShowUpdateReadyBanner(versionTag);
            }
        }

        private async Task ShowWhatsNewIfJustUpdatedAsync()
        {
            try
            {
                await Task.Delay(1500); // Let the window/shell finish standing up first
                var updateService = _services.GetRequiredService<IUpdateService>();
                var currentVersion = updateService.CurrentVersionString;

                var lastShown = Utilities.UpdateStateStore.GetLastWhatsNewVersionShown();
                if (string.IsNullOrEmpty(lastShown))
                {
                    // First run ever with this marker - nothing to announce, just start tracking
                    // from here so the *next* real update shows its notes.
                    Utilities.UpdateStateStore.SetLastWhatsNewVersionShown(currentVersion);
                    return;
                }

                if (lastShown == currentVersion) return;

                // Persist the marker before the network round-trip below, not after. A slow or
                // rate-limited GitHub call (the anonymous API is capped at 60 req/hour per IP) threw
                // past the old post-fetch write on some launches, so the marker never updated and the
                // dialog kept reappearing every single launch instead of once per version.
                Utilities.UpdateStateStore.SetLastWhatsNewVersionShown(currentVersion);

                var notes = string.IsNullOrWhiteSpace(AppConstants.GitHubRepository)
                    ? null
                    : await updateService.GetReleaseNotesForVersionAsync(AppConstants.GitHubRepository, currentVersion);

                if (Current?.Windows.Count > 0 && Current.Windows[0].Page is Shell shell)
                {
                    var message = string.IsNullOrWhiteSpace(notes)
                        ? "Procure has been updated. Check Settings for release details."
                        : notes;
                    await shell.DisplayAlertAsync($"What's New in v{currentVersion}", message, "OK");
                }
            }
            catch
            {
                // Silently ignore - this is a nice-to-have, never worth blocking startup over.
            }
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var shell = _services.GetRequiredService<AppShell>();
            // No explicit Width/Height: OnWindowCreated maximizes the window, and an explicit size
            // applied after that only fights the maximized state.
            var window = new Window(shell)
            {
                Title = "RWC MM Tracker",
                MinimumWidth = 800,
                MinimumHeight = 550
            };

#if DEBUG
            // Opt-in only: PROCURE_BOARD_SELFCHECK=1. Placed here rather than the constructor -
            // AppShell (just built above) is what constructs the PrListPageModel singleton this check
            // needs, and the constructor runs before AppShell exists.
            if (Environment.GetEnvironmentVariable("PROCURE_BOARD_SELFCHECK") == "1")
            {
                _ = Utilities.BoardMemorySelfCheck.RunAsync();
            }

            // Opt-in only: PROCURE_ACCENT_SELFCHECK=1. Cycles every accent in both modes, then restores.
            if (Environment.GetEnvironmentVariable("PROCURE_ACCENT_SELFCHECK") == "1")
            {
                _ = Utilities.AccentSelfCheck.RunAsync();
            }

            // Opt-in only: PROCURE_THEME_SELFCHECK=1. Switches the theme for real and back again.
            if (Environment.GetEnvironmentVariable("PROCURE_THEME_SELFCHECK") == "1")
            {
                _ = Utilities.ThemeTransitionSelfCheck.RunAsync();
            }

            // Opt-in only: PROCURE_PRINT_SELFCHECK=1. Measures installed drivers; prints nothing.
            if (Environment.GetEnvironmentVariable("PROCURE_PRINT_SELFCHECK") == "1")
            {
                _ = Utilities.PrintGeometrySelfCheck.RunAsync();
            }
#endif

            return window;
        }
    }
}