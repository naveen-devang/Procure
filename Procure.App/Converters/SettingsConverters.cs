using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Procure.App.Converters;

/// <summary>Accent-swatch fill. The selected swatch is the same hue at a much higher alpha,
/// so it composites brighter over whichever theme background is behind it - no ring, no plate,
/// and (unlike a selection visual state) it repaints itself on a light/dark switch.</summary>
public static class Swatch
{
    public static SolidColorBrush Fill(bool selected, string hex) =>
        new(HexBrushConverter.Parse(hex, selected ? (byte)0xC0 : (byte)0x24));
    // ponytail: alphas are eyeballed against both theme grounds; tune here if a hue reads weak.
}

/// <summary>#RRGGBB / #AARRGGBB hex string -> SolidColorBrush. Lets the accent-swatch
/// palette paint itself straight from AccentPalettes (Core, UI-framework-free) instead
/// of eight hand-written buttons with baked-in hex.</summary>
sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        new SolidColorBrush(Parse(value as string, null));

    /// <summary>alphaOverride replaces whatever alpha the hex carries (null keeps it).</summary>
    internal static Color Parse(string? hex, byte? alphaOverride)
    {
        var s = hex?.TrimStart('#');
        if (string.IsNullOrEmpty(s)) return Microsoft.UI.Colors.Transparent;
        byte a = 255;
        if (s.Length == 8) { a = System.Convert.ToByte(s[..2], 16); s = s[2..]; }
        return Color.FromArgb(alphaOverride ?? a,
            System.Convert.ToByte(s.Substring(0, 2), 16),
            System.Convert.ToByte(s.Substring(2, 2), 16),
            System.Convert.ToByte(s.Substring(4, 2), 16));
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
