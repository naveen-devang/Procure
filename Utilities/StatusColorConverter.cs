using System;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.Models;
using static Procure.Utilities.ThemeHelper;

namespace Procure.Utilities
{
    public static class ThemeHelper
    {
        // Cached resolved theme. A bool holds no UI reference, so it cannot leak.
        private static bool? _isDark;

        /// <summary>Clears the cached theme so the next read re-resolves it.</summary>
        public static void Invalidate() => _isDark = null;

        public static bool IsDark => _isDark ??= Resolve();

        // Microsoft.Maui.Graphics.Color is a class, so Color.FromArgb re-parses the hex string AND
        // heap-allocates on every call - and these converters run per bound property, per card, per
        // container recycle, i.e. on every scroll tick. Interning costs one parse per distinct hex per
        // process. Same fix PoFulfillmentPalette already applies to its three converters, generalised
        // so the other eighteen do not each need a hand-named field per colour.
        private static readonly ConcurrentDictionary<string, Color> HexCache = new();

        /// <summary>Cached <see cref="Color.FromArgb"/>. Only for compile-time hex literals - a cache
        /// keyed on runtime-varying strings would grow without bound.</summary>
        public static Color Hex(string argb) => HexCache.GetOrAdd(argb, static s => Color.FromArgb(s));

        private static bool Resolve()
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

    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var status = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            return status switch
            {
                ProcurementStatus.Delivered or ProcurementStatus.Closed or "Signed" or "True" =>
                    isDark ? Hex("#6CCB5F") : Hex("#107C41"),

                ProcurementStatus.PcrApproved =>
                    isDark ? Hex("#60CDFF") : Hex("#004E8C"),

                ProcurementStatus.PoRaised or ProcurementStatus.PartiallyDelivered =>
                    isDark ? Hex("#FCE100") : Hex("#7A4B04"),

                ProcurementStatus.PcrSubmitted or ProcurementStatus.QuotesReceived =>
                    isDark ? Hex("#C4A6FE") : Hex("#4B2BA8"),

                ProcurementStatus.RfqSent =>
                    isDark ? Hex("#4DD0E1") : Hex("#005A70"),

                ProcurementStatus.PrRaised =>
                    isDark ? Hex("#D2D0CE") : Hex("#494847"),

                ProcurementStatus.OnHold =>
                    isDark ? Hex("#FFA043") : Hex("#8A3B00"),

                ProcurementStatus.Cancelled =>
                    isDark ? Hex("#FF99A4") : Hex("#A80000"),

                ProcurementStatus.Merged =>
                    isDark ? Hex("#C8C6C4") : Hex("#494847"),

                _ => isDark ? Hex("#D2D0CE") : Hex("#494847")
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
                    isDark ? Hex("#133824") : Hex("#E7F3ED"),

                ProcurementStatus.PcrApproved =>
                    isDark ? Hex("#142F4C") : Hex("#EBF3FC"),

                ProcurementStatus.PoRaised or ProcurementStatus.PartiallyDelivered =>
                    isDark ? Hex("#3B2E08") : Hex("#FFF4CE"),

                ProcurementStatus.PcrSubmitted or ProcurementStatus.QuotesReceived =>
                    isDark ? Hex("#281A4C") : Hex("#F0EBF9"),

                ProcurementStatus.RfqSent =>
                    isDark ? Hex("#103B44") : Hex("#E0F7FA"),

                ProcurementStatus.PrRaised =>
                    isDark ? Hex("#2D2C2C") : Hex("#F3F2F1"),

                ProcurementStatus.OnHold =>
                    isDark ? Hex("#3D2008") : Hex("#FFF0E6"),

                ProcurementStatus.Cancelled =>
                    isDark ? Hex("#3F1011") : Hex("#FDE7E9"),

                ProcurementStatus.Merged =>
                    isDark ? Hex("#2D2C2C") : Hex("#EDEBE9"),

                _ => isDark ? Hex("#2D2C2C") : Hex("#F3F2F1")
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
