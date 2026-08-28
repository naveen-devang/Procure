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
#endif
        }

        private static void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
            => Procure.Utilities.ThemeHelper.Invalidate();

        private async Task CheckForUpdatesInBackgroundAsync()
        {
            try
            {
                await Task.Delay(3000); // Allow UI to initialize first
                var updateService = _services.GetRequiredService<IUpdateService>();
                if (!string.IsNullOrWhiteSpace(AppConstants.GitHubRepository))
                {
                    var update = await updateService.CheckForUpdatesAsync(AppConstants.GitHubRepository);
                    if (update.IsUpdateAvailable && Current?.Windows.Count > 0)
                    {
                        var shell = Current.Windows[0].Page as Shell;
                        if (shell != null)
                        {
                            var view = await shell.DisplayAlertAsync(
                                "Update Available",
                                $"A new version of Procure ({update.TagName}) is available. Would you like to view update details in Settings?",
                                "View in Settings",
                                "Later");

                            if (view)
                            {
                                await shell.GoToAsync("//settings");
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silently ignore background check failures on startup
            }
        }

        private const string LastWhatsNewVersionShownKey = "LastWhatsNewVersionShown";

        private async Task ShowWhatsNewIfJustUpdatedAsync()
        {
            try
            {
                await Task.Delay(1500); // Let the window/shell finish standing up first
                var updateService = _services.GetRequiredService<IUpdateService>();
                var currentVersion = updateService.CurrentVersionString;

                var lastShown = Microsoft.Maui.Storage.Preferences.Default.Get(LastWhatsNewVersionShownKey, string.Empty);
                if (string.IsNullOrEmpty(lastShown))
                {
                    // First run ever with this preference - nothing to announce, just start
                    // tracking from here so the *next* real update shows its notes.
                    Microsoft.Maui.Storage.Preferences.Default.Set(LastWhatsNewVersionShownKey, currentVersion);
                    return;
                }

                if (lastShown == currentVersion) return;

                var notes = string.IsNullOrWhiteSpace(AppConstants.GitHubRepository)
                    ? null
                    : await updateService.GetReleaseNotesForVersionAsync(AppConstants.GitHubRepository, currentVersion);

                Microsoft.Maui.Storage.Preferences.Default.Set(LastWhatsNewVersionShownKey, currentVersion);

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
#endif

            return window;
        }
    }
}