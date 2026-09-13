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

/// <summary>Two-way text for a numeric property.
///
/// An `x:Bind ... Mode=TwoWay` straight onto a decimal goes through
/// XamlBindingHelper.ConvertValue, which does not fail gracefully: clearing the box, or a
/// half-typed "1." or "-", takes down the process with an access violation inside coreclr
/// (seen entering a transport rate in the PO dialog). This parses instead, and maps anything
/// unparseable onto a value the property can actually hold - null for a nullable, zero
/// otherwise - so a cleared box means "no value" rather than a crash.
///
/// Currency text is read too ("AED 1,200", "$50"), as the MAUI boxes did. A half-typed percentage
/// ("5%") leaves the value alone: MoneyBox.PercentOf turns it into an amount.
///
/// A {Binding} (unlike x:Bind) does not say what type it is writing to, so those pass
/// ConverterParameter=decimal? (or decimal) to say it.
///
/// While a box has the cursor, its text is left as typed. Writing the value pushes it straight back
/// into the box, so without this "1." became "1", "AED" vanished as it was typed, and the cursor jumped
/// to the start - typing "AED 1,200" produced "0021".</summary>
public sealed class NumericTextConverter : IValueConverter
{
    private static Procure.App.Platform.ShellContext? _shell;

    public object Convert(object value, Type t, object p, string l)
    {
        if (TypedTextFor(value) is { } typed) return typed;
        return value?.ToString() ?? string.Empty;
    }

    /// <summary>The focused box's own text, when it already stands for this value (or is still being typed
    /// towards one: an unfinished "5%", or text with no number in it yet).</summary>
    private static string? TypedTextFor(object? value)
    {
        _shell ??= App.Services.GetService(typeof(Procure.App.Platform.ShellContext)) as Procure.App.Platform.ShellContext;
        if (_shell?.XamlRoot is not { } root
            || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is not Microsoft.UI.Xaml.Controls.TextBox box) return null;

        var text = box.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains('%')) return text;
        if (!Procure.App.Platform.MoneyBox.TryParseAmount(text, out var typed)) return value is null ? text : null;
        return value switch
        {
            decimal d when d == typed => text,
            double f when (decimal)f == typed => text,
            int i when i == typed => text,
            _ => null,
        };
    }

    public object ConvertBack(object value, Type t, object p, string l)
    {
        var s = (value as string)?.Trim();
        if (p is "decimal?") t = typeof(decimal?);
        else if (p is "decimal") t = typeof(decimal);
        var nullable = Nullable.GetUnderlyingType(t) is not null;
        var target = Nullable.GetUnderlyingType(t) ?? t;

        if (!string.IsNullOrEmpty(s))
        {
            if (s.Contains('%')) return Microsoft.UI.Xaml.DependencyProperty.UnsetValue;
            if (Procure.App.Platform.MoneyBox.TryParseAmount(s, out var d))
            {
                if (target == typeof(decimal)) return d;
                if (target == typeof(double)) return (double)d;
                if (target == typeof(int) && d == Math.Truncate(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
            }
        }

        if (nullable) return null!;
        if (target == typeof(decimal)) return 0m;
        if (target == typeof(double)) return 0d;
        if (target == typeof(int)) return 0;
        return value;
    }
}

/// <summary>Colours the allocation running total on a PO line: amber while some of the quantity has
/// no transport contract, critical when more has been allocated than ordered, accent when it
/// balances. The value is the line's unallocated quantity.</summary>
public sealed class AllocationColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var res = Application.Current.Resources;
        var remaining = value is decimal d ? d : 0m;
        var key = remaining > 0m ? "FluentCaution" : remaining < 0m ? "FluentCritical" : "PrimaryTextBrush";
        return res[key];
    }

    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

/// <summary>Pixel count -> GridLength. Procure.Core is UI-framework-free, so a column width that
/// the view model decides comes across as a plain number.</summary>
public sealed class PixelsToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        new GridLength(value is double d ? d : 0d, GridUnitType.Pixel);

    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}
