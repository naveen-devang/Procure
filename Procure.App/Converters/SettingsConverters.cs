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
/// A box's text is left as typed. Writing the value pushes it straight back into the same box, so without
/// this "1." became "1" and the cursor jumped to the start - typing "AED 1,200" produced "0021".
///
/// Only the number box being typed in keeps its text: ConvertBack notes which focused box it read from,
/// and Convert hands that box's text back only while that box still has the cursor and its text stands
/// for the very number being shown. The first version copied the text of whatever box had the cursor -
/// a vendor name typed or pasted appeared in every empty price, discount and last-price box, and in a
/// quantity box whose value matched digits in the name. Text without a number is never handed back.</summary>
public sealed class NumericTextConverter : IValueConverter
{
    private static Procure.App.Platform.ShellContext? _shell;
    private static WeakReference<Microsoft.UI.Xaml.Controls.TextBox>? _typingBox;   // UI thread only

    private static Microsoft.UI.Xaml.Controls.TextBox? FocusedTextBox()
    {
        _shell ??= App.Services.GetService(typeof(Procure.App.Platform.ShellContext)) as Procure.App.Platform.ShellContext;
        return _shell?.XamlRoot is { } root
            ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as Microsoft.UI.Xaml.Controls.TextBox
            : null;
    }

    public object Convert(object value, Type t, object p, string l)
    {
        if (value is not null
            && _typingBox is not null && _typingBox.TryGetTarget(out var typing)
            && ReferenceEquals(FocusedTextBox(), typing) && typing.Text is { Length: > 0 } text
            && !text.Contains('%')
            && Procure.App.Platform.MoneyBox.TryParseAmount(text, out var typed)
            && value switch { decimal d => d == typed, double f => (decimal)f == typed, int i => i == typed, _ => false })
            return text;
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type t, object p, string l)
    {
        // The box being typed in is the focused one; remember it so only it keeps its own text.
        if (FocusedTextBox() is { } box) _typingBox = new WeakReference<Microsoft.UI.Xaml.Controls.TextBox>(box);
        return ParseBack(value, t, p);
    }

    private static object ParseBack(object value, Type t, object p)
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
