using System;
using System.Globalization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.Models;

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
                return isDark ? Color.FromArgb("#FF99A4") : Color.FromArgb("#A80000");
            }
            return isDark ? Color.FromArgb("#D2D0CE") : Color.FromArgb("#494847");
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
                return isDark ? Color.FromArgb("#3F1011") : Color.FromArgb("#FDE7E9");
            }
            return isDark ? Color.FromArgb("#2D2C2C") : Color.FromArgb("#F3F2F1");
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
                return isDark ? Color.FromArgb("#5C1A1C") : Color.FromArgb("#F1B0B7");
            }
            return isDark ? Color.FromArgb("#3D3B39") : Color.FromArgb("#E0DFDD");
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
                return isDark ? Color.FromArgb("#6CCB5F") : Color.FromArgb("#107C41");
            }
            return isDark ? Color.FromArgb("#A19F9D") : Color.FromArgb("#605E5C");
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
                return isDark ? Color.FromArgb("#FFC83B") : Color.FromArgb("#8A5700");
            }
            if (text.Contains("Approved", StringComparison.OrdinalIgnoreCase))
            {
                return isDark ? Color.FromArgb("#6CCB5F") : Color.FromArgb("#107C41");
            }
            return isDark ? Color.FromArgb("#A19F9D") : Color.FromArgb("#605E5C");
        }

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

    public class BoolToFilterChipBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (value is bool b && b)
            {
                return isDark ? Color.FromArgb("#142F4C") : Color.FromArgb("#EBF3FC");
            }
            return isDark ? Color.FromArgb("#2B2B2B") : Color.FromArgb("#FFFFFF");
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
                return isDark ? Color.FromArgb("#60CDFF") : Color.FromArgb("#0078D4");
            }
            return isDark ? Color.FromArgb("#404040") : Color.FromArgb("#B0B0B0");
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
                return isDark ? Color.FromArgb("#60CDFF") : Color.FromArgb("#004E8C");
            }
            return isDark ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#1A1A1A");
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
                    return isDark ? Color.FromArgb("#6CCB5F") : Color.FromArgb("#107C41");
                if (text.StartsWith("Sent", StringComparison.OrdinalIgnoreCase) || text.Contains("Pending", StringComparison.OrdinalIgnoreCase))
                    return isDark ? Color.FromArgb("#FFC83B") : Color.FromArgb("#8A5700");
                return isDark ? Color.FromArgb("#9E9E9E") : Color.FromArgb("#757575");
            }

            if (value is bool b && b) return isDark ? Color.FromArgb("#6CCB5F") : Color.FromArgb("#107C41");
            return isDark ? Color.FromArgb("#9E9E9E") : Color.FromArgb("#757575");
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
                return isDark ? Color.FromArgb("#60CDFF") : Color.FromArgb("#0078D4");
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
                return isDark ? Color.FromArgb("#60CDFF") : Color.FromArgb("#0078D4");
            }
            return isDark ? Color.FromArgb("#606060") : Color.FromArgb("#A0A0A0");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToApprovalInclusionCheckColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            return isDark ? Color.FromArgb("#000000") : Color.FromArgb("#FFFFFF");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PoFulfillmentColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;
            if (text.Contains("Exceeds", StringComparison.OrdinalIgnoreCase) || text.Contains("Over-allocated", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#FF99A4") : Color.FromArgb("#A80000");
            if (text.Contains("Pending", StringComparison.OrdinalIgnoreCase) || text.Contains("Partial", StringComparison.OrdinalIgnoreCase) || text.Contains("Unordered", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#FFC83B") : Color.FromArgb("#8A5700");
            if (text.Contains("Complete", StringComparison.OrdinalIgnoreCase) || text.Contains("Fully Allocated", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#6CCB5F") : Color.FromArgb("#107C41");
            return isDark ? Color.FromArgb("#D2D0CE") : Color.FromArgb("#494847");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PoFulfillmentBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;
            if (text.Contains("Exceeds", StringComparison.OrdinalIgnoreCase) || text.Contains("Over-allocated", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#3F1011") : Color.FromArgb("#FDE7E9");
            if (text.Contains("Pending", StringComparison.OrdinalIgnoreCase) || text.Contains("Partial", StringComparison.OrdinalIgnoreCase) || text.Contains("Unordered", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#3B2E08") : Color.FromArgb("#FFF4CE");
            if (text.Contains("Complete", StringComparison.OrdinalIgnoreCase) || text.Contains("Fully Allocated", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#143823") : Color.FromArgb("#E7F3ED");
            return isDark ? Color.FromArgb("#2D2C2C") : Color.FromArgb("#F3F2F1");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PoFulfillmentStrokeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string ?? string.Empty;
            var isDark = ThemeHelper.IsDark;
            if (text.Contains("Exceeds", StringComparison.OrdinalIgnoreCase) || text.Contains("Over-allocated", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#5C1A1C") : Color.FromArgb("#F1B0B7");
            if (text.Contains("Pending", StringComparison.OrdinalIgnoreCase) || text.Contains("Partial", StringComparison.OrdinalIgnoreCase) || text.Contains("Unordered", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#5C4910") : Color.FromArgb("#FFE28A");
            if (text.Contains("Complete", StringComparison.OrdinalIgnoreCase) || text.Contains("Fully Allocated", StringComparison.OrdinalIgnoreCase))
                return isDark ? Color.FromArgb("#275A38") : Color.FromArgb("#A3D9B8");
            return isDark ? Color.FromArgb("#3D3B39") : Color.FromArgb("#E0DFDD");
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
                ProcurementPrType.StoresAndSpares => isDark ? Color.FromArgb("#4DD0E1") : Color.FromArgb("#00687A"), // Aqua / Deep Cyan
                ProcurementPrType.RawMaterial     => isDark ? Color.FromArgb("#FFB968") : Color.FromArgb("#8A3B00"), // Warm Amber / Bronze
                ProcurementPrType.PackingMaterial => isDark ? Color.FromArgb("#52BE80") : Color.FromArgb("#0B8A5A"), // Emerald / Mint Green
                ProcurementPrType.Service         => isDark ? Color.FromArgb("#D7BDE2") : Color.FromArgb("#7D3C98"), // Violet / Plum
                ProcurementPrType.Capex           => isDark ? Color.FromArgb("#F1948A") : Color.FromArgb("#A93226"), // Crimson / Coral Red
                _                                 => isDark ? Color.FromArgb("#D2D0CE") : Color.FromArgb("#494847")  // Neutral Slate
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
                ProcurementPrType.StoresAndSpares => isDark ? Color.FromArgb("#002D38") : Color.FromArgb("#E0F7FA"),
                ProcurementPrType.RawMaterial     => isDark ? Color.FromArgb("#3B2200") : Color.FromArgb("#FFF4CE"),
                ProcurementPrType.PackingMaterial => isDark ? Color.FromArgb("#0B2F25") : Color.FromArgb("#E8F8F5"),
                ProcurementPrType.Service         => isDark ? Color.FromArgb("#2E1437") : Color.FromArgb("#F4ECF7"),
                ProcurementPrType.Capex           => isDark ? Color.FromArgb("#3D1414") : Color.FromArgb("#FDEDEC"),
                _                                 => isDark ? Color.FromArgb("#2D2C2C") : Color.FromArgb("#F3F2F1")
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
                ProcurementPrType.StoresAndSpares => isDark ? Color.FromArgb("#006073") : Color.FromArgb("#80DEEA"),
                ProcurementPrType.RawMaterial     => isDark ? Color.FromArgb("#663C00") : Color.FromArgb("#FFE082"),
                ProcurementPrType.PackingMaterial => isDark ? Color.FromArgb("#196F3D") : Color.FromArgb("#A3E4D7"),
                ProcurementPrType.Service         => isDark ? Color.FromArgb("#6C3483") : Color.FromArgb("#D2B4DE"),
                ProcurementPrType.Capex           => isDark ? Color.FromArgb("#78281F") : Color.FromArgb("#F5B7B1"),
                _                                 => isDark ? Color.FromArgb("#3D3B39") : Color.FromArgb("#E0DFDD")
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
