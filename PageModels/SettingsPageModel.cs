using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Procure.Data;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;

namespace Procure.PageModels
{
    public partial class SettingsPageModel : ObservableObject
    {
        private readonly ISettingsService _settingsService;
        private readonly IUpdateService _updateService;
        private readonly SeedDataService _seedDataService;
        private readonly SqliteDatabase _sqliteDb;
        private readonly IErrorHandler _errorHandler;
        private readonly IKeyboardShortcutService _keyboardShortcutService;

        /// <summary>Backs the Custom Columns section, which moved here from its own sidebar tab. It is a
        /// DI singleton with its own commands and its own Columns collection; the section's LazyExpander
        /// template binds straight to this rather than to the page model.</summary>
        public ManageColumnsPageModel ColumnsModel { get; }

        // Built once from the registry - each row watches the shared service itself, so nothing here
        // needs to refresh them individually when a binding changes or a recording completes.
        public ObservableCollection<ShortcutRowViewModel> ShortcutRows { get; }

        /// <summary>The one instance DI ever constructs (AddSingleton). Section item-templates realised
        /// inside a LazyExpander.ContentTemplate sit in a nested template namescope where {x:Reference}
        /// to the page does not resolve; they reach page-level commands through this static instead,
        /// the same pattern PrListPageModel.Current serves on the board.</summary>
        public static SettingsPageModel? Current { get; private set; }

        /// <summary>Which settings category the right-hand pane shows. Each section's LazyExpander builds
        /// its content the first time this matches; a section never selected in a session is never built.</summary>
        [ObservableProperty]
        public partial string SelectedSection { get; set; } = "Appearance";

        [RelayCommand]
        public void SelectSection(string section)
        {
            if (!string.IsNullOrWhiteSpace(section)) SelectedSection = section;
        }

        partial void OnSelectedSectionChanged(string value)
        {
            // The Custom Columns list used to load on that page's OnAppearing; now it loads the first
            // (and every) time you open the section. LoadColumnsAsync guards its own re-entry and the
            // query is a handful of rows.
            if (value == "Columns") _ = ColumnsModel.LoadColumnsAsync();
        }

        [ObservableProperty]
        public partial string SelectedThemeMode { get; set; } = "Dark";

        [ObservableProperty]
        public partial string SelectedAccentTheme { get; set; } = "Blue";

        [ObservableProperty]
        public partial int UrgentDays { get; set; }

        [ObservableProperty]
        public partial int NormalDays { get; set; }

        [ObservableProperty]
        public partial string DatabasePath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DatabaseDirectory { get; set; } = string.Empty;

        // Sidebar & Display Options
        [ObservableProperty]
        public partial bool IsSidebarCompact { get; set; }

        [ObservableProperty]
        public partial bool AutoCollapseSidebarOnNarrow { get; set; } = true;

        [ObservableProperty]
        public partial bool IsRawPackingTabEnabled { get; set; }

        // Default Currency
        [ObservableProperty]
        public partial string DefaultCurrency { get; set; } = "AED";

        public IReadOnlyList<string> AvailableCurrencies => AppConstants.SupportedCurrencies;

        // Updates State
        [ObservableProperty]
        public partial bool AutoCheckUpdates { get; set; } = true;

        [ObservableProperty]
        public partial string CurrentVersion { get; set; } = "v1.0.0";

        [ObservableProperty]
        public partial bool IsCheckingForUpdates { get; set; }

        [ObservableProperty]
        public partial bool IsUpdateAvailable { get; set; }

        [ObservableProperty]
        public partial UpdateInfo? UpdateInfo { get; set; }

        [ObservableProperty]
        public partial string UpdateStatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsStatusError { get; set; }

        [ObservableProperty]
        public partial bool IsDownloading { get; set; }

        [ObservableProperty]
        public partial double DownloadProgress { get; set; }

        [ObservableProperty]
        public partial string DownloadPercentageText { get; set; } = "0%";

        // Default Approval Stages
        [ObservableProperty]
        public partial ObservableCollection<string> DefaultApprovalStages { get; set; } = new();

        // Transient inline confirmation beside the Save button; OS toasts throw on unpackaged apps.
        [ObservableProperty]
        public partial string SavedMessage { get; set; } = string.Empty;

        private int _savedMessageGeneration;

        [ObservableProperty]
        public partial string NewDefaultStageName { get; set; } = string.Empty;

        public IReadOnlyList<PastelThemeOption> AvailableAccentThemes => _settingsService.AvailableAccentThemes;

        public SettingsPageModel(
            ISettingsService settingsService,
            IUpdateService updateService,
            SeedDataService seedDataService,
            SqliteDatabase sqliteDb,
            IErrorHandler errorHandler,
            IKeyboardShortcutService keyboardShortcutService,
            ManageColumnsPageModel columnsModel)
        {
            _settingsService = settingsService;
            _updateService = updateService;
            _seedDataService = seedDataService;
            _sqliteDb = sqliteDb;
            _errorHandler = errorHandler;
            _keyboardShortcutService = keyboardShortcutService;
            ColumnsModel = columnsModel;
            Current = this;

            ShortcutRows = new ObservableCollection<ShortcutRowViewModel>(
                Procure.Utilities.KeyboardShortcutRegistry.All.Select(d => new ShortcutRowViewModel(d, _keyboardShortcutService)));

            SelectedThemeMode = _settingsService.AppTheme;
            SelectedAccentTheme = _settingsService.AccentTheme;
            UrgentDays = _settingsService.UrgentOverdueDays;
            NormalDays = _settingsService.NormalOverdueDays;
            DatabaseDirectory = _settingsService.DatabaseDirectory;
            DatabasePath = DatabaseConstants.DatabaseFilePath;
            AutoCheckUpdates = _settingsService.AutoCheckUpdatesOnStartup;
            IsSidebarCompact = _settingsService.IsSidebarCompact;
            AutoCollapseSidebarOnNarrow = _settingsService.AutoCollapseSidebarOnNarrow;
            IsRawPackingTabEnabled = _settingsService.IsRawPackingTabEnabled;
            DefaultCurrency = _settingsService.DefaultCurrency;
            CurrentVersion = $"v{_updateService.CurrentVersionString}";

            LoadDefaultApprovalStages();

            _settingsService.SettingsChanged += (s, e) =>
            {
                // Keyed like AppShell's handler: a theme click or a sidebar auto-collapse during a
                // window resize must not touch unrelated properties or rebuild the stages list.
                switch (e.Key)
                {
                    case nameof(ISettingsService.AppTheme):
                        SelectedThemeMode = _settingsService.AppTheme;
                        break;
                    case nameof(ISettingsService.AccentTheme):
                        SelectedAccentTheme = _settingsService.AccentTheme;
                        break;
                    case nameof(ISettingsService.AutoCheckUpdatesOnStartup):
                        AutoCheckUpdates = _settingsService.AutoCheckUpdatesOnStartup;
                        break;
                    case nameof(ISettingsService.IsSidebarCompact):
                        IsSidebarCompact = _settingsService.IsSidebarCompact;
                        break;
                    case nameof(ISettingsService.AutoCollapseSidebarOnNarrow):
                        AutoCollapseSidebarOnNarrow = _settingsService.AutoCollapseSidebarOnNarrow;
                        break;
                    case nameof(ISettingsService.IsRawPackingTabEnabled):
                        IsRawPackingTabEnabled = _settingsService.IsRawPackingTabEnabled;
                        break;
                    case nameof(ISettingsService.DefaultCurrency):
                        DefaultCurrency = _settingsService.DefaultCurrency;
                        break;
                    case nameof(ISettingsService.GetDefaultApprovalRoles):
                        // This model's own stage commands mutate DefaultApprovalStages in place and
                        // then persist, which raises this key - only a genuinely different list
                        // (another writer) warrants the Clear-and-refill rebuild.
                        if (!DefaultApprovalStages.SequenceEqual(_settingsService.GetDefaultApprovalRoles()))
                            LoadDefaultApprovalStages();
                        break;
                }
            };
        }

        private void LoadDefaultApprovalStages()
        {
            var roles = _settingsService.GetDefaultApprovalRoles();
            DefaultApprovalStages.Clear();
            foreach (var r in roles)
            {
                DefaultApprovalStages.Add(r);
            }
        }

        partial void OnDefaultCurrencyChanged(string value)
        {
            if (_settingsService.DefaultCurrency != value && !string.IsNullOrWhiteSpace(value))
            {
                _settingsService.DefaultCurrency = value;
            }
        }

        partial void OnIsSidebarCompactChanged(bool value)
        {
            if (_settingsService.IsSidebarCompact != value)
            {
                _settingsService.IsSidebarCompact = value;
            }
        }

        partial void OnAutoCollapseSidebarOnNarrowChanged(bool value)
        {
            if (_settingsService.AutoCollapseSidebarOnNarrow != value)
            {
                _settingsService.AutoCollapseSidebarOnNarrow = value;
            }
        }

        partial void OnIsRawPackingTabEnabledChanged(bool value)
        {
            if (_settingsService.IsRawPackingTabEnabled != value)
            {
                _settingsService.IsRawPackingTabEnabled = value;
            }
        }

        [RelayCommand]
        public async Task SelectThemeModeAsync(string mode)
        {
            SelectedThemeMode = mode;
            if (_settingsService.AppTheme == mode) return;

            // Same curtain/fade the sidebar buttons get; the fallback covers a missing shell.
            if (Shell.Current is AppShell shell)
                await shell.TransitionThemeAsync(mode);
            else
                _settingsService.AppTheme = mode;
        }

        [RelayCommand]
        public void SelectAccentTheme(string accentId)
        {
            SelectedAccentTheme = accentId;
            _settingsService.AccentTheme = accentId;
        }

        [RelayCommand]
        public async Task CheckForUpdatesAsync()
        {
            if (IsCheckingForUpdates || IsDownloading) return;

            try
            {
                IsCheckingForUpdates = true;
                IsStatusError = false;
                UpdateStatusMessage = "Checking for new releases...";

                var result = await _updateService.CheckForUpdatesAsync(AppConstants.GitHubRepository);
                UpdateInfo = result;

                if (result.IsUpdateAvailable)
                {
                    IsUpdateAvailable = true;
                    UpdateStatusMessage = $"New version {result.TagName} is available.";
                }
                else
                {
                    IsUpdateAvailable = false;
                    UpdateStatusMessage = $"You are up to date. ({CurrentVersion})";
                }
            }
            catch (Exception ex)
            {
                IsStatusError = true;
                IsUpdateAvailable = false;
                UpdateStatusMessage = $"Failed to check for updates: {ex.Message}";
            }
            finally
            {
                IsCheckingForUpdates = false;
            }
        }

        [RelayCommand]
        public async Task DownloadAndInstallUpdateAsync()
        {
            if (UpdateInfo == null || IsDownloading) return;

            if (string.IsNullOrWhiteSpace(UpdateInfo.DownloadUrl))
            {
                // Fallback to release page
                await OpenReleasePageAsync();
                return;
            }

            try
            {
                IsDownloading = true;
                DownloadProgress = 0.0;
                DownloadPercentageText = "0%";
                UpdateStatusMessage = $"Downloading {UpdateInfo.AssetName}...";

                var progressReporter = new Progress<double>(p =>
                {
                    DownloadProgress = p;
                    DownloadPercentageText = $"{p * 100:F0}%";
                });

                var installerPath = await _updateService.DownloadUpdateAsync(UpdateInfo, progressReporter);

                UpdateStatusMessage = "Download complete! Launching installer...";

                var launched = _updateService.LaunchInstaller(installerPath);
                if (launched)
                {
                    if (Shell.Current != null)
                    {
                        var exit = await Shell.Current.DisplayAlertAsync(
                            "Installer Launched",
                            "The update installer has been launched. Would you like to close the app to proceed with installation?",
                            "Close App",
                            "Later");

                        if (exit && Application.Current != null)
                        {
                            Application.Current.Quit();
                        }
                    }
                }
                else
                {
                    // Fallback to explorer/browser
                    await OpenReleasePageAsync();
                }
            }
            catch (Exception ex)
            {
                IsStatusError = true;
                UpdateStatusMessage = $"Download failed: {ex.Message}";
                _errorHandler.HandleError(ex);
            }
            finally
            {
                IsDownloading = false;
            }
        }

        [RelayCommand]
        public async Task OpenReleasePageAsync()
        {
            if (UpdateInfo != null && !string.IsNullOrWhiteSpace(UpdateInfo.ReleaseUrl))
            {
                await _updateService.OpenReleaseInBrowserAsync(UpdateInfo.ReleaseUrl);
            }
            else
            {
                var url = $"https://github.com/{AppConstants.GitHubRepository}/releases";
                await _updateService.OpenReleaseInBrowserAsync(url);
            }
        }

        [RelayCommand]
        public async Task SaveSettingsAsync()
        {
            try
            {
                _settingsService.UrgentOverdueDays = UrgentDays;
                _settingsService.NormalOverdueDays = NormalDays;
                _settingsService.DatabaseDirectory = DatabaseDirectory;
                _settingsService.AutoCheckUpdatesOnStartup = AutoCheckUpdates;

                SavedMessage = "Settings saved";
                var gen = ++_savedMessageGeneration;
                Microsoft.Maui.Dispatching.Dispatcher.GetForCurrentThread()
                    ?.DispatchDelayed(TimeSpan.FromMilliseconds(2500), () =>
                    {
                        if (gen == _savedMessageGeneration) SavedMessage = string.Empty;
                    });
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task OpenDatabaseFolderAsync()
        {
            try
            {
                var dir = DatabaseConstants.DatabaseDirectory;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task ChangeDatabaseLocationAsync()
        {
            if (Shell.Current == null) return;

            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(GetActiveWindow());
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;

                var newDir = folder.Path;
                if (string.Equals(newDir, DatabaseConstants.DatabaseDirectory, StringComparison.OrdinalIgnoreCase))
                    return;

                // FolderPicker only returns folders that already exist, but existence doesn't guarantee
                // write access (e.g. a protected system folder) - catch that now rather than after the
                // restart, where the only recovery is editing Preferences by hand.
                try
                {
                    var probePath = Path.Combine(newDir, $".procure-write-check-{Guid.NewGuid():N}");
                    File.WriteAllText(probePath, string.Empty);
                    File.Delete(probePath);
                }
                catch (Exception writeEx)
                {
                    await Shell.Current.DisplayAlertAsync("Can't Use This Folder",
                        $"Procure can't write to that folder: {writeEx.Message}", "OK");
                    return;
                }

                var existingDbPath = Path.Combine(newDir, DatabaseConstants.DatabaseFilename);
                var message = File.Exists(existingDbPath)
                    ? $"Procure will restart and load the existing database found at:\n\n{existingDbPath}\n\nEverything currently loaded (PRs, boards, dashboard) will be released - your app settings stay as they are."
                    : $"Procure will restart and create a new, empty database at:\n\n{existingDbPath}\n\nEverything currently loaded (PRs, boards, dashboard) will be released - your app settings stay as they are.";

                var confirm = await Shell.Current.DisplayAlertAsync(
                    "Change Database Location", message, "Restart & Switch", "Cancel");
                if (!confirm) return;

                _settingsService.DatabaseDirectory = newDir;
                RestartApplication();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // Relaunches the same executable and ends this process. A DB directory change touches data
        // every singleton page/pagemodel already loaded into memory (PR Board, Dashboard, Call-Off,
        // Manage Columns, per-row detail panels) - restarting is what actually releases all of it in one
        // guaranteed step, rather than hunting down and resetting each cache individually. Settings are
        // Preferences-backed (Windows registry), entirely separate from the SQLite file, so they survive.
        private static void RestartApplication()
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
            }
            Environment.Exit(0);
        }

        private static Microsoft.UI.Xaml.Window GetActiveWindow()
            => (Microsoft.UI.Xaml.Window)Microsoft.Maui.Controls.Application.Current!.Windows[0].Handler!.PlatformView!;

        [RelayCommand]
        public async Task ResetDatabaseAsync()
        {
            if (Shell.Current == null) return;

            var confirm = await Shell.Current.DisplayAlertAsync(
                "Reset & Re-Seed Database",
                "This will clear the current database and repopulate it with fresh sample procurement data. Are you sure?",
                "Yes, Reset",
                "Cancel");

            if (!confirm) return;

            try
            {
                var dbPath = DatabaseConstants.DatabaseFilePath;
                _sqliteDb.ResetInitialization();

                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }

                await _sqliteDb.InitializeAsync();
                await _seedDataService.EnsureDataSeededAsync();

                await Shell.Current.DisplayAlertAsync(
                    "Database Reset",
                    "Database reset and re-seeded successfully with realistic demo data.",
                    "OK");
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void AddNewDefaultStage()
        {
            if (string.IsNullOrWhiteSpace(NewDefaultStageName)) return;

            DefaultApprovalStages.Add(NewDefaultStageName.Trim());
            _settingsService.SetDefaultApprovalRoles(DefaultApprovalStages);
            NewDefaultStageName = string.Empty;
        }

        [RelayCommand]
        public void RemoveDefaultStage(string stageName)
        {
            DefaultApprovalStages.Remove(stageName);
            _settingsService.SetDefaultApprovalRoles(DefaultApprovalStages);
        }

        [RelayCommand]
        public void MoveDefaultStageUp(string stageName)
        {
            int index = DefaultApprovalStages.IndexOf(stageName);
            if (index > 0)
            {
                DefaultApprovalStages.Move(index, index - 1);
                _settingsService.SetDefaultApprovalRoles(DefaultApprovalStages);
            }
        }

        [RelayCommand]
        public void MoveDefaultStageDown(string stageName)
        {
            int index = DefaultApprovalStages.IndexOf(stageName);
            if (index >= 0 && index < DefaultApprovalStages.Count - 1)
            {
                DefaultApprovalStages.Move(index, index + 1);
                _settingsService.SetDefaultApprovalRoles(DefaultApprovalStages);
            }
        }

        [RelayCommand]
        public void ResetDefaultStagesToPreset()
        {
            DefaultApprovalStages.Clear();
            DefaultApprovalStages.Add(ApprovalRoles.ProcurementManager);
            DefaultApprovalStages.Add(ApprovalRoles.FinanceController);
            DefaultApprovalStages.Add(ApprovalRoles.Cfo);
            DefaultApprovalStages.Add(ApprovalRoles.Ceo);
            _settingsService.SetDefaultApprovalRoles(DefaultApprovalStages);
        }

        [RelayCommand]
        public void ResetAllShortcuts() => _keyboardShortcutService.ResetAllToDefaults();
    }
}
