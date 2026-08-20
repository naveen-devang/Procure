using System;
using System.Globalization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.Models;

namespace Procure.Utilities
{
    public static class ThemeHelper
    {
        public static bool IsDark
        {
            get
            {
                try
                {
                    var app = Application.Current;
                    if (app != null)
                    {
                        if (app.UserAppTheme == AppTheme.Dark) return true;
                        if (app.UserAppTheme == AppTheme.Light) return false;
                    }

                    var savedTheme = Preferences.Default.Get("Procure_AppTheme", string.Empty);
                    if (string.Equals(savedTheme, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(savedTheme, "Light", StringComparison.OrdinalIgnoreCase)) return false;

                    if (app != null)
                    {
                        if (app.RequestedTheme == AppTheme.Dark) return true;
                        if (app.RequestedTheme == AppTheme.Light) return false;
                        if (app.PlatformAppTheme == AppTheme.Dark) return true;
                        if (app.PlatformAppTheme == AppTheme.Light) return false;
                    }

#if WINDOWS
                    var uiSettings = new Windows.UI.ViewManagement.UISettings();
                    var color = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                    return color.R < 128;
#endif
                }
                catch { }
                return false;
            }
        }
    }

    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var status = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            return status switch
            {
                ProcurementStatus.Delivered or ProcurementStatus.Closed or "Signed" or "True" =>
                    isDark ? Color.FromArgb("#6CCB5F") : Color.FromArgb("#107C41"),

                ProcurementStatus.PcrApproved =>
                    isDark ? Color.FromArgb("#60CDFF") : Color.FromArgb("#004E8C"),

                ProcurementStatus.PoRaised or ProcurementStatus.PartiallyDelivered =>
                    isDark ? Color.FromArgb("#FCE100") : Color.FromArgb("#7A4B04"),

                ProcurementStatus.PcrSubmitted or ProcurementStatus.QuotesReceived =>
                    isDark ? Color.FromArgb("#C4A6FE") : Color.FromArgb("#4B2BA8"),

                ProcurementStatus.RfqSent =>
                    isDark ? Color.FromArgb("#4DD0E1") : Color.FromArgb("#005A70"),

                ProcurementStatus.PrRaised =>
                    isDark ? Color.FromArgb("#D2D0CE") : Color.FromArgb("#494847"),

                ProcurementStatus.OnHold =>
                    isDark ? Color.FromArgb("#FFA043") : Color.FromArgb("#8A3B00"),

                ProcurementStatus.Cancelled =>
                    isDark ? Color.FromArgb("#FF99A4") : Color.FromArgb("#A80000"),

                ProcurementStatus.Merged =>
                    isDark ? Color.FromArgb("#C8C6C4") : Color.FromArgb("#494847"),

                _ => isDark ? Color.FromArgb("#D2D0CE") : Color.FromArgb("#494847")
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class StatusBadgeBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var status = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            return status switch
            {
                ProcurementStatus.Delivered or ProcurementStatus.Closed or "Signed" or "True" =>
                    isDark ? Color.FromArgb("#133824") : Color.FromArgb("#E7F3ED"),

                ProcurementStatus.PcrApproved =>
                    isDark ? Color.FromArgb("#142F4C") : Color.FromArgb("#EBF3FC"),

                ProcurementStatus.PoRaised or ProcurementStatus.PartiallyDelivered =>
                    isDark ? Color.FromArgb("#3B2E08") : Color.FromArgb("#FFF4CE"),

                ProcurementStatus.PcrSubmitted or ProcurementStatus.QuotesReceived =>
                    isDark ? Color.FromArgb("#281A4C") : Color.FromArgb("#F0EBF9"),

                ProcurementStatus.RfqSent =>
                    isDark ? Color.FromArgb("#103B44") : Color.FromArgb("#E0F7FA"),

                ProcurementStatus.PrRaised =>
                    isDark ? Color.FromArgb("#2D2C2C") : Color.FromArgb("#F3F2F1"),

                ProcurementStatus.OnHold =>
                    isDark ? Color.FromArgb("#3D2008") : Color.FromArgb("#FFF0E6"),

                ProcurementStatus.Cancelled =>
                    isDark ? Color.FromArgb("#3F1011") : Color.FromArgb("#FDE7E9"),

                ProcurementStatus.Merged =>
                    isDark ? Color.FromArgb("#2D2C2C") : Color.FromArgb("#EDEBE9"),

                _ => isDark ? Color.FromArgb("#2D2C2C") : Color.FromArgb("#F3F2F1")
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
