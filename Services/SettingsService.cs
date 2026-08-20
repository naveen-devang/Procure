using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using Procure.Data;
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

        public event EventHandler? SettingsChanged;

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
            get => Preferences.Default.Get(KeyUrgentDays, 5);
            set
            {
                Preferences.Default.Set(KeyUrgentDays, value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public int NormalOverdueDays
        {
            get => Preferences.Default.Get(KeyNormalDays, 10);
            set
            {
                Preferences.Default.Set(KeyNormalDays, value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string DatabaseDirectory
        {
            get => DatabaseConstants.DatabaseDirectory;
            set
            {
                DatabaseConstants.DatabaseDirectory = value;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string AppTheme
        {
            get => Preferences.Default.Get(KeyAppTheme, "Dark");
            set
            {
                Preferences.Default.Set(KeyAppTheme, value);
                ApplyThemeMode(value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string AccentTheme
        {
            get => Preferences.Default.Get(KeyAccentTheme, "Blue");
            set
            {
                Preferences.Default.Set(KeyAccentTheme, value);
                ApplyAccentColor(value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool AutoCheckUpdatesOnStartup
        {
            get => Preferences.Default.Get(KeyAutoCheckUpdates, true);
            set
            {
                Preferences.Default.Set(KeyAutoCheckUpdates, value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsSidebarCompact
        {
            get => Preferences.Default.Get(KeySidebarCompact, false);
            set
            {
                Preferences.Default.Set(KeySidebarCompact, value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool AutoCollapseSidebarOnNarrow
        {
            get => Preferences.Default.Get(KeyAutoCollapseOnNarrow, true);
            set
            {
                Preferences.Default.Set(KeyAutoCollapseOnNarrow, value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string DefaultCurrency
        {
            get => Preferences.Default.Get(KeyDefaultCurrency, "AED");
            set
            {
                Preferences.Default.Set(KeyDefaultCurrency, value);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public List<string> GetDefaultApprovalRoles()
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

        public void SetDefaultApprovalRoles(IEnumerable<string> roles)
        {
            var str = string.Join("|||", roles.Where(r => !string.IsNullOrWhiteSpace(r)));
            Preferences.Default.Set(KeyDefaultApprovalRoles, str);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
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
