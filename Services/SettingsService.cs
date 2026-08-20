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
                DatabaseConstants.DatabaseDirectory = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(DatabaseDirectory)));
            }
        }

        public string AppTheme
        {
            get => _appTheme ??= Preferences.Default.Get(KeyAppTheme, "Dark");
            set
            {
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
                Preferences.Default.Set(KeyAutoCollapseOnNarrow, value);
                _autoCollapseSidebarOnNarrow = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(AutoCollapseSidebarOnNarrow)));
            }
        }

        public string DefaultCurrency
        {
            get => _defaultCurrency ??= Preferences.Default.Get(KeyDefaultCurrency, "AED");
            set
            {
                Preferences.Default.Set(KeyDefaultCurrency, value);
                _defaultCurrency = value;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(nameof(DefaultCurrency)));
            }
        }

        public List<string> GetDefaultApprovalRoles() => new(_defaultApprovalRoles ??= LoadDefaultApprovalRoles());

        public void SetDefaultApprovalRoles(IEnumerable<string> roles)
        {
            var list = roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
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
        }

        private void ApplyAccentColor(string accentId)
        {
            if (Application.Current == null) return;

            var palette = PastelPalettes.FirstOrDefault(p => p.Id.Equals(accentId, StringComparison.OrdinalIgnoreCase))
                          ?? PastelPalettes[0];

            Application.Current.Resources["Primary"] = palette.LightColor;
            Application.Current.Resources["PrimaryDark"] = palette.DarkColor;
            Application.Current.Resources["SecondaryDarkText"] = palette.DarkColor;
            Application.Current.Resources["FluentPrimaryBg"] = palette.BgColor;
            Application.Current.Resources["FluentInfo"] = palette.DarkColor;
            Application.Current.Resources["FocusStroke"] = palette.LightColor;
            Application.Current.Resources["PrimaryBrush"] = new SolidColorBrush(palette.LightColor);

            foreach (var dict in Application.Current.Resources.MergedDictionaries)
            {
                dict["Primary"] = palette.LightColor;
                dict["PrimaryDark"] = palette.DarkColor;
                dict["SecondaryDarkText"] = palette.DarkColor;
                dict["FluentPrimaryBg"] = palette.BgColor;
                dict["FluentInfo"] = palette.DarkColor;
                dict["FocusStroke"] = palette.LightColor;
                dict["PrimaryBrush"] = new SolidColorBrush(palette.LightColor);
            }
        }
    }
}
