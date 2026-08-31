using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace Procure.Utilities
{
    // Drives the combined Select/Deselect-All button label: true (all selected) -> "Deselect All",
    // false -> "Select All". An optional ConverterParameter is appended (e.g. "Items").
    public class SelectionToggleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var allSelected = value is bool b && b;
            var verb = allSelected ? "Deselect All" : "Select All";
            var suffix = parameter as string;
            return string.IsNullOrWhiteSpace(suffix) ? verb : $"{verb} {suffix}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
