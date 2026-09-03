using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.Models;
using static Procure.Utilities.ThemeHelper;

namespace Procure.Utilities
{
    // Task priority -> the 3px row stripe / detail accent. Semantic colours (green / amber /
    // red), deliberately separate from the pastel app accent, matching how status is shown
    // elsewhere. None is a faint neutral rather than nothing so every row keeps its rail.
    public class TodoPriorityColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            return value switch
            {
                TodoPriority.High => isDark ? Hex("#FF9385") : Hex("#F2604E"),
                TodoPriority.Medium => isDark ? Hex("#FFC062") : Hex("#F0A526"),
                TodoPriority.Low => isDark ? Hex("#7FDCAB") : Hex("#39B87A"),
                _ => isDark ? Hex("#9A9A9A") : Hex("#CFCFCF"),
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    // Shared by the view toggle (bool value) and the priority picker (enum value + string
    // ConverterParameter). "Active" = the value is true, or its ToString() matches the parameter.
    internal static class SegmentState
    {
        public static bool IsActive(object? value, object? parameter)
            => value is bool b ? b
             : string.Equals(value?.ToString(), parameter as string, StringComparison.OrdinalIgnoreCase);
    }

    // Active segment: a raised surface on the track. Inactive: transparent.
    public class TodoSegmentBgConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (!SegmentState.IsActive(value, parameter)) return Colors.Transparent;
            return ThemeHelper.IsDark ? Hex("#3A3A3C") : Hex("#FFFFFF");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    // Active segment text: full-strength ink. Inactive: muted.
    public class TodoSegmentTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (SegmentState.IsActive(value, parameter)) return isDark ? Hex("#F3F2F1") : Hex("#1A1A1A");
            return isDark ? Hex("#9A9A9A") : Hex("#6B6B6B");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    // Due-date chip text colour: red when overdue, muted otherwise.
    public class TodoOverdueColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isDark = ThemeHelper.IsDark;
            if (value is bool overdue && overdue) return isDark ? Hex("#E0796D") : Hex("#B5493F");
            return isDark ? Hex("#A9B0BD") : Hex("#605E5C");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
