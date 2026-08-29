using System;
using System.Globalization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.Models;
using static Procure.Utilities.ThemeHelper;

namespace Procure.Utilities
{
    public class PriorityColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var priority = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            if (priority.Equals(ProcurementPriority.Urgent, StringComparison.OrdinalIgnoreCase))
            {
                return isDark ? Hex("#FF99A4") : Hex("#A80000");
            }
            return isDark ? Hex("#D2D0CE") : Hex("#494847");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PriorityBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var priority = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            if (priority.Equals(ProcurementPriority.Urgent, StringComparison.OrdinalIgnoreCase))
            {
                return isDark ? Hex("#3F1011") : Hex("#FDE7E9");
            }
            return isDark ? Hex("#2D2C2C") : Hex("#F3F2F1");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PriorityBorderConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var priority = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            if (priority.Equals(ProcurementPriority.Urgent, StringComparison.OrdinalIgnoreCase))
            {
                return isDark ? Hex("#5C1A1C") : Hex("#F1B0B7");
            }
            return isDark ? Hex("#3D3B39") : Hex("#E0DFDD");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PoPillTextColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (value is decimal d && d > 0)
            {
                return isDark ? Hex("#6CCB5F") : Hex("#107C41");
            }
            return isDark ? Hex("#A19F9D") : Hex("#605E5C");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PcrPillTextColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            if (text.Contains("Pending", StringComparison.OrdinalIgnoreCase))
            {
                return isDark ? Hex("#FFC83B") : Hex("#8A5700");
            }
            if (text.Contains("Approved", StringComparison.OrdinalIgnoreCase))
            {
                return isDark ? Hex("#6CCB5F") : Hex("#107C41");
            }
            return isDark ? Hex("#A19F9D") : Hex("#605E5C");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>Highlights whichever option button matches the bound value - the Settings page's Color
    /// Mode and Pastel Accent buttons all shared one fixed BorderWidth, so none of them ever visibly
    /// showed which was selected. Bind BorderWidth through this with ConverterParameter set to the
    /// button's own CommandParameter; 2 when it matches the selection, 0 otherwise.</summary>
    public class StringEqualsToBorderWidthConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase) ? 2.0 : 0.0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>True when the bound string equals the ConverterParameter (case-insensitive). Drives the
    /// Settings page section panes: each LazyExpander's IsExpanded binds through this against
    /// SelectedSection, so only the chosen section is built and the rest cost nothing.</summary>
    public class StringEqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string s) return !string.IsNullOrWhiteSpace(s);
            return value != null;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToInvertedBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    // Collapses a Grid column's width/spacing to 0 rather than just hiding its content - a Grid's
    // Auto/fixed column keeps reserving space for an invisible child, only binding the dimension
    // itself actually removes it. ConverterParameter is "trueValue|falseValue".
    public class BoolToDoubleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && parameter is string s)
            {
                var parts = s.Split('|');
                if (parts.Length == 2 && double.TryParse(parts[b ? 0 : 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                    return result;
            }
            return 0d;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class IntGreaterThanZeroConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int i && i > 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    // Drives a list row's "selected" highlight directly off object identity instead of a
    // CollectionView SelectionMode + VisualStateManager "Selected" state - that combination is
    // unreliable on WinUI (the highlight only partially renders), where a plain equality check is
    // deterministic regardless of platform VSM support.
    public class ReferenceEqualsConverter : IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
            => values.Length == 2 && ReferenceEquals(values[0], values[1]);

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToFilterChipBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (value is bool b && b)
            {
                return isDark ? Hex("#142F4C") : Hex("#EBF3FC");
            }
            return isDark ? Hex("#2B2B2B") : Hex("#FFFFFF");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToFilterChipStrokeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (value is bool b && b)
            {
                return isDark ? Hex("#60CDFF") : Hex("#0078D4");
            }
            return isDark ? Hex("#404040") : Hex("#B0B0B0");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToFilterChipTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (value is bool b && b)
            {
                return isDark ? Hex("#60CDFF") : Hex("#004E8C");
            }
            return isDark ? Hex("#FFFFFF") : Hex("#1A1A1A");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToChevronConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return "\uE70E"; // ChevronUp
            }
            return "\uE70D"; // ChevronDown
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToApprovalStatusColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;

            if (value is string text)
            {
                if (text.StartsWith("Received", StringComparison.OrdinalIgnoreCase) || text.Contains("Signed", StringComparison.OrdinalIgnoreCase))
                    return isDark ? Hex("#6CCB5F") : Hex("#107C41");
                if (text.StartsWith("Sent", StringComparison.OrdinalIgnoreCase) || text.Contains("Pending", StringComparison.OrdinalIgnoreCase))
                    return isDark ? Hex("#FFC83B") : Hex("#8A5700");
                return isDark ? Hex("#9E9E9E") : Hex("#757575");
            }

            if (value is bool b && b) return isDark ? Hex("#6CCB5F") : Hex("#107C41");
            return isDark ? Hex("#9E9E9E") : Hex("#757575");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToApprovalInclusionBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (value is bool b && b)
            {
                return isDark ? Hex("#60CDFF") : Hex("#0078D4");
            }
            return Colors.Transparent;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToApprovalInclusionStrokeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (value is bool b && b)
            {
                return isDark ? Hex("#60CDFF") : Hex("#0078D4");
            }
            return isDark ? Hex("#606060") : Hex("#A0A0A0");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToApprovalInclusionCheckColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            return isDark ? Hex("#000000") : Hex("#FFFFFF");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    // Shared by the three PoFulfillment converters: one text classification plus pre-parsed Color
    // singletons. Color.FromArgb re-parsed a hex string and allocated per conversion, and the PO
    // wizard's banner runs four of these converters on every recalculation.
    internal static class PoFulfillmentPalette
    {
        internal const int Over = 0, Pending = 1, Complete = 2, Neutral = 3;

        internal static int Classify(object? value)
        {
            var text = value as string ?? string.Empty;
            if (text.Contains("Exceeds", StringComparison.OrdinalIgnoreCase) || text.Contains("Over-allocated", StringComparison.OrdinalIgnoreCase))
                return Over;
            if (text.Contains("Pending", StringComparison.OrdinalIgnoreCase) || text.Contains("Partial", StringComparison.OrdinalIgnoreCase) || text.Contains("Unordered", StringComparison.OrdinalIgnoreCase))
                return Pending;
            if (text.Contains("Complete", StringComparison.OrdinalIgnoreCase) || text.Contains("Fully Allocated", StringComparison.OrdinalIgnoreCase))
                return Complete;
            return Neutral;
        }

        internal static readonly Color[] TextDark = { Color.FromArgb("#FF99A4"), Color.FromArgb("#FFC83B"), Color.FromArgb("#6CCB5F"), Color.FromArgb("#D2D0CE") };
        internal static readonly Color[] TextLight = { Color.FromArgb("#A80000"), Color.FromArgb("#8A5700"), Color.FromArgb("#107C41"), Color.FromArgb("#494847") };
        internal static readonly Color[] BgDark = { Color.FromArgb("#3F1011"), Color.FromArgb("#3B2E08"), Color.FromArgb("#143823"), Color.FromArgb("#2D2C2C") };
        internal static readonly Color[] BgLight = { Color.FromArgb("#FDE7E9"), Color.FromArgb("#FFF4CE"), Color.FromArgb("#E7F3ED"), Color.FromArgb("#F3F2F1") };
        internal static readonly Color[] StrokeDark = { Color.FromArgb("#5C1A1C"), Color.FromArgb("#5C4910"), Color.FromArgb("#275A38"), Color.FromArgb("#3D3B39") };
        internal static readonly Color[] StrokeLight = { Color.FromArgb("#F1B0B7"), Color.FromArgb("#FFE28A"), Color.FromArgb("#A3D9B8"), Color.FromArgb("#E0DFDD") };
    }

    public class PoFulfillmentColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var state = PoFulfillmentPalette.Classify(value);
            return ThemeHelper.IsDark ? PoFulfillmentPalette.TextDark[state] : PoFulfillmentPalette.TextLight[state];
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PoFulfillmentBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var state = PoFulfillmentPalette.Classify(value);
            return ThemeHelper.IsDark ? PoFulfillmentPalette.BgDark[state] : PoFulfillmentPalette.BgLight[state];
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PoFulfillmentStrokeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var state = PoFulfillmentPalette.Classify(value);
            return ThemeHelper.IsDark ? PoFulfillmentPalette.StrokeDark[state] : PoFulfillmentPalette.StrokeLight[state];
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PrTypeColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var prType = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            return prType switch
            {
                ProcurementPrType.StoresAndSpares => isDark ? Hex("#4DD0E1") : Hex("#00687A"), // Aqua / Deep Cyan
                ProcurementPrType.RawMaterial     => isDark ? Hex("#FFB968") : Hex("#8A3B00"), // Warm Amber / Bronze
                ProcurementPrType.PackingMaterial => isDark ? Hex("#52BE80") : Hex("#0B8A5A"), // Emerald / Mint Green
                ProcurementPrType.Service         => isDark ? Hex("#D7BDE2") : Hex("#7D3C98"), // Violet / Plum
                ProcurementPrType.Capex           => isDark ? Hex("#F1948A") : Hex("#A93226"), // Crimson / Coral Red
                _                                 => isDark ? Hex("#D2D0CE") : Hex("#494847")  // Neutral Slate
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PrTypeBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var prType = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            return prType switch
            {
                ProcurementPrType.StoresAndSpares => isDark ? Hex("#002D38") : Hex("#E0F7FA"),
                ProcurementPrType.RawMaterial     => isDark ? Hex("#3B2200") : Hex("#FFF4CE"),
                ProcurementPrType.PackingMaterial => isDark ? Hex("#0B2F25") : Hex("#E8F8F5"),
                ProcurementPrType.Service         => isDark ? Hex("#2E1437") : Hex("#F4ECF7"),
                ProcurementPrType.Capex           => isDark ? Hex("#3D1414") : Hex("#FDEDEC"),
                _                                 => isDark ? Hex("#2D2C2C") : Hex("#F3F2F1")
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PrTypeBorderConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var prType = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;

            return prType switch
            {
                ProcurementPrType.StoresAndSpares => isDark ? Hex("#006073") : Hex("#80DEEA"),
                ProcurementPrType.RawMaterial     => isDark ? Hex("#663C00") : Hex("#FFE082"),
                ProcurementPrType.PackingMaterial => isDark ? Hex("#196F3D") : Hex("#A3E4D7"),
                ProcurementPrType.Service         => isDark ? Hex("#6C3483") : Hex("#D2B4DE"),
                ProcurementPrType.Capex           => isDark ? Hex("#78281F") : Hex("#F5B7B1"),
                _                                 => isDark ? Hex("#3D3B39") : Hex("#E0DFDD")
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
