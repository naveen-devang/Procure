using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Procure.App.Converters;

/// <summary>#RRGGBB / #AARRGGBB hex string -> SolidColorBrush. Lets the accent-swatch
/// GridView paint itself straight from AccentPalettes (Core, UI-framework-free) instead
/// of eight hand-written buttons with baked-in hex.</summary>
sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var s = (value as string)?.TrimStart('#');
        if (string.IsNullOrEmpty(s)) return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        byte a = 255;
        if (s.Length == 8) { a = System.Convert.ToByte(s[..2], 16); s = s[2..]; }
        return new SolidColorBrush(Color.FromArgb(a,
            System.Convert.ToByte(s.Substring(0, 2), 16),
            System.Convert.ToByte(s.Substring(2, 2), 16),
            System.Convert.ToByte(s.Substring(4, 2), 16)));
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

/// <summary>value == ConverterParameter (string, case-insensitive) -> Visibility.
/// Drives the Settings section panes off SelectedSection.</summary>
sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}
