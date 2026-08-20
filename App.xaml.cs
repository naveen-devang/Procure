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

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var shell = _services.GetRequiredService<AppShell>();
            var window = new Window(shell)
            {
                Title = "RWC  MM Tracker",
                Width = 1400,
                Height = 900,
                MinimumWidth = 800,
                MinimumHeight = 550
            };
            return window;
        }
    }
}