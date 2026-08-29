using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using Procure.Models;

namespace Procure.Services
{
    public class SettingsService : ISettingsService
    {
        private const string KeyUrgentDays = "Procure_UrgentDays";
        private const string KeyNormalDays = "Procure_NormalDays";
        private const string KeyAppTheme = "Procure_AppTheme";
        private const string KeyAccentTheme = "Procure_AccentTheme";
        private const string KeyAutoCheckUpdates = "Procure_AutoCheckUpdates";
        private const string KeyDefaultApprovalRoles = "Procure_DefaultApprovalRoles";
        private const string KeySidebarCompact = "Procure_SidebarCompact";
        private const string KeyAutoCollapseOnNarrow = "Procure_AutoCollapseOnNarrow";
        private const string KeyDefaultCurrency = "Procure_DefaultCurrency";
        private const string KeyRawPackingTabEnabled = "Procure_RawPackingTabEnabled";

        // Cached values. Preferences (a registry read on unpackaged WinUI) is hit at most
        // once per key, lazily on first get; setters keep the cache in sync afterwards.
        private int? _urgentOverdueDays;
        private int? _normalOverdueDays;
        private string? _appTheme;
        private string? _accentTheme;
        private bool? _autoCheckUpdatesOnStartup;
        private bool? _isSidebarCompact;
        private bool? _autoCollapseSidebarOnNarrow;
        private string? _defaultCurrency;
        private List<string>? _defaultApprovalRoles;
        private bool? _isRawPackingTabEnabled;

        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

        public static readonly List<PastelThemeOption> PastelPalettes = new()
        {
            new PastelThemeOption
            {
                Id = "Blue",
                Name = "Pastel Blue",
                LightColor = Color.FromArgb("#3A82EE"),
                DarkColor = Color.FromArgb("#60CDFF"),
                BgColor = Color.FromArgb("#243A82EE")
            },
            new PastelThemeOption
            {
                Id = "Purple",
                Name = "Pastel Purple",
                LightColor = Color.FromArgb("#8B6CE8"),
                DarkColor = Color.FromArgb("#B198F0"),
                BgColor = Color.FromArgb("#248B6CE8")
            },
            new PastelThemeOption
            {
                Id = "Mint",
                Name = "Pastel Mint",
                LightColor = Color.FromArgb("#2E9E6D"),
                DarkColor = Color.FromArgb("#6CCB5F"),
                BgColor = Color.FromArgb("#242E9E6D")
            },
            new PastelThemeOption
            {
                Id = "Coral",
                Name = "Pastel Coral",
                LightColor = Color.FromArgb("#E07238"),
                DarkColor = Color.FromArgb("#FFA043"),
                BgColor = Color.FromArgb("#24E07238")
            },
            new PastelThemeOption
            {
                Id = "Pink",
                Name = "Pastel Rose",
                LightColor = Color.FromArgb("#D95382"),
                DarkColor = Color.FromArgb("#FF99A4"),
                BgColor = Color.FromArgb("#24D95382")
            },
            new PastelThemeOption
            {
                Id = "Red",
                Name = "Pastel Crimson",
                LightColor = Color.FromArgb("#C83E4D"),
                DarkColor = Color.FromArgb("#FF7B7B"),
                BgColor = Color.FromArgb("#24C83E4D")
            },
            new PastelThemeOption
            {
                Id = "Yellow",
                Name = "Pastel Amber",
                LightColor = Color.FromArgb("#D48B17"),
                DarkColor = Color.FromArgb("#FCE100"),
                BgColor = Color.FromArgb("#24D48B17")
            },
            new PastelThemeOption
            {
                Id = "Teal",
                Name = "Pastel Teal",
                LightColor = Color.FromArgb("#1E989B"),
                DarkColor = Color.FromArgb("#48CAE4"),
                BgColor = Color.FromArgb("#241E989B")
            }
        };

        public IReadOnlyList<PastelThemeOption> AvailableAccentThemes => PastelPalettes;

        public int UrgentOverdueDays
        {
            get => _urgentOverdueDays ??= Preferences.Default.Get(KeyUrgentDays, 5);
            set
            {
                // Unchanged-value guard (every setter here): a no-op "Save Settings" used to fire
                // events that refiltered the whole PR board.
                if (UrgentOverdueDays == value) return;
                Preferences.Default.Set(KeyUrgentDays, value);
                _urgentOverdueDays = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(UrgentOverdueDays)));
            }
        }

        public int NormalOverdueDays
        {
            get => _normalOverdueDays ??= Preferences.Default.Get(KeyNormalDays, 10);
            set
            {
                if (NormalOverdueDays == value) return;
                Preferences.Default.Set(KeyNormalDays, value);
                _normalOverdueDays = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(NormalOverdueDays)));
            }
        }

        public string DatabaseDirectory
        {
            get => DatabaseConstants.DatabaseDirectory;
            set
            {
                if (DatabaseConstants.DatabaseDirectory == value) return;
                DatabaseConstants.DatabaseDirectory = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(DatabaseDirectory)));
            }
        }

        public string AppTheme
        {
            get => _appTheme ??= Preferences.Default.Get(KeyAppTheme, "Dark");
            set
            {
                if (AppTheme == value) return;
                Preferences.Default.Set(KeyAppTheme, value);
                _appTheme = value;
                ApplyThemeMode(value);
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(AppTheme)));
            }
        }

        public string AccentTheme
        {
            get => _accentTheme ??= Preferences.Default.Get(KeyAccentTheme, "Blue");
            set
            {
                if (AccentTheme == value) return;
                Preferences.Default.Set(KeyAccentTheme, value);
                _accentTheme = value;
                ApplyAccentColor(value);
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(AccentTheme)));
            }
        }

        public bool AutoCheckUpdatesOnStartup
        {
            get => _autoCheckUpdatesOnStartup ??= Preferences.Default.Get(KeyAutoCheckUpdates, true);
            set
            {
                if (AutoCheckUpdatesOnStartup == value) return;
                Preferences.Default.Set(KeyAutoCheckUpdates, value);
                _autoCheckUpdatesOnStartup = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(AutoCheckUpdatesOnStartup)));
            }
        }

        public bool IsSidebarCompact
        {
            get => _isSidebarCompact ??= Preferences.Default.Get(KeySidebarCompact, false);
            set
            {
                if (IsSidebarCompact == value) return;
                Preferences.Default.Set(KeySidebarCompact, value);
                _isSidebarCompact = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(IsSidebarCompact)));
            }
        }

        public bool AutoCollapseSidebarOnNarrow
        {
            get => _autoCollapseSidebarOnNarrow ??= Preferences.Default.Get(KeyAutoCollapseOnNarrow, true);
            set
            {
                if (AutoCollapseSidebarOnNarrow == value) return;
                Preferences.Default.Set(KeyAutoCollapseOnNarrow, value);
                _autoCollapseSidebarOnNarrow = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(AutoCollapseSidebarOnNarrow)));
            }
        }

        // Off by default - most installs don't track Raw/Packing Material call-offs, and leaving
        // it off costs nothing extra since CallOffPageModel/Repository are DI singletons that
        // don't even get constructed until the tab is first opened.
        public bool IsRawPackingTabEnabled
        {
            get => _isRawPackingTabEnabled ??= Preferences.Default.Get(KeyRawPackingTabEnabled, false);
            set
            {
                if (IsRawPackingTabEnabled == value) return;
                Preferences.Default.Set(KeyRawPackingTabEnabled, value);
                _isRawPackingTabEnabled = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(IsRawPackingTabEnabled)));
            }
        }

        public string DefaultCurrency
        {
            get => _defaultCurrency ??= Preferences.Default.Get(KeyDefaultCurrency, "AED");
            set
            {
                if (DefaultCurrency == value) return;
                Preferences.Default.Set(KeyDefaultCurrency, value);
                _defaultCurrency = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(DefaultCurrency)));
            }
        }

        public List<string> GetDefaultApprovalRoles() => new(_defaultApprovalRoles ??= LoadDefaultApprovalRoles());

        public void SetDefaultApprovalRoles(IEnumerable<string> roles)
        {
            var list = roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
            if (list.SequenceEqual(_defaultApprovalRoles ??= LoadDefaultApprovalRoles())) return;
            Preferences.Default.Set(KeyDefaultApprovalRoles, string.Join("|||", list));
            _defaultApprovalRoles = list;
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(GetDefaultApprovalRoles)));
        }

        private static List<string> LoadDefaultApprovalRoles()
        {
            var raw = Preferences.Default.Get(KeyDefaultApprovalRoles, string.Empty);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var list = raw.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                if (list.Count > 0) return list;
            }
            return new List<string>
            {
                ApprovalRoles.ProcurementManager,
                ApprovalRoles.FinanceController,
                ApprovalRoles.Cfo,
                ApprovalRoles.Ceo
            };
        }

        public void ApplySavedTheme()
        {
            ApplyThemeMode(AppTheme);
            ApplyAccentColor(AccentTheme);
        }

        private void ApplyThemeMode(string themeMode)
        {
            if (Application.Current == null) return;

            Application.Current.UserAppTheme = themeMode switch
            {
                "Light" => Microsoft.Maui.ApplicationModel.AppTheme.Light,
                "Dark" => Microsoft.Maui.ApplicationModel.AppTheme.Dark,
                _ => Microsoft.Maui.ApplicationModel.AppTheme.Unspecified
            };

            ThemeHelper.Invalidate();

            // Primary is theme-dependent (see ApplyAccentColor), so a theme change re-resolves it.
            // Reached from the AppTheme setter, which runs under ThemeCurtain - nothing flashes.
            ApplyAccentColor(AccentTheme);
        }

        private void ApplyAccentColor(string accentId)
        {
            if (Application.Current == null) return;

            var palette = PastelPalettes.FirstOrDefault(p => p.Id.Equals(accentId, StringComparison.OrdinalIgnoreCase))
                          ?? PastelPalettes[0];

            // Each accent carries two colours. The pastel (DarkColor) is what the picker shows and is
            // now what every FILL gets - button backgrounds, toggle tracks, checkboxes, spinners - in
            // both modes, via AccentFill. It used to be written only to PrimaryDark, which nothing but
            // a few Dark-mode text/icon spots read, so the pastel was painted on nothing but the swatch.
            //
            // Primary stays for text, borders and icons on a plain background, where a pastel on white
            // is unreadable at text size: deep in Light, pastel in Dark. It follows the theme because
            // ApplyThemeMode re-runs this. (FluentPrimaryBg/FluentInfo/FocusStroke/SecondaryDarkText
            // were also written here; nothing reads them - dropped.)
            var primary = ThemeHelper.IsDark ? palette.DarkColor : palette.LightColor;

            Application.Current.Resources["Primary"] = primary;
            Application.Current.Resources["PrimaryDark"] = palette.DarkColor;
            Application.Current.Resources["AccentFill"] = palette.DarkColor;
            Application.Current.Resources["PrimaryBrush"] = new SolidColorBrush(primary);

            foreach (var dict in Application.Current.Resources.MergedDictionaries)
            {
                dict["Primary"] = primary;
                dict["PrimaryDark"] = palette.DarkColor;
                dict["AccentFill"] = palette.DarkColor;
                dict["PrimaryBrush"] = new SolidColorBrush(primary);
            }
        }
    }
}
