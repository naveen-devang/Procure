using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Procure.App.Converters;

// WinUI ports of the PO-fulfilment + approval-status converters (Utilities/PriorityColorConverter.cs,
// StatusColorConverter.cs). Return SolidColorBrush; theme-varying via BoardTheme.IsDark.

file static class PoFulfil
{
    // Over, Pending, Complete, Neutral
    public static int Classify(object? value)
    {
        var t = value as string ?? "";
        if (t.Contains("Exceeds", StringComparison.OrdinalIgnoreCase) || t.Contains("Over-allocated", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Over-ordered", StringComparison.OrdinalIgnoreCase)) return 0;
        if (t.Contains("Pending", StringComparison.OrdinalIgnoreCase) || t.Contains("Partial", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Unordered", StringComparison.OrdinalIgnoreCase) || t.Contains("Missing", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Qty changed", StringComparison.OrdinalIgnoreCase) || t.Contains("Not on the requisition", StringComparison.OrdinalIgnoreCase)) return 1;
        if (t.Contains("Complete", StringComparison.OrdinalIgnoreCase) || t.Contains("Fully Allocated", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    public static readonly string[] TextDark = { "#FF99A4", "#FFC83B", "#6CCB5F", "#D2D0CE" };
    public static readonly string[] TextLight = { "#A80000", "#8A5700", "#107C41", "#494847" };
    public static readonly string[] BgDark = { "#3F1011", "#3B2E08", "#143823", "#2D2C2C" };
    public static readonly string[] BgLight = { "#FDE7E9", "#FFF4CE", "#E7F3ED", "#F3F2F1" };
    public static readonly string[] StrokeDark = { "#5C1A1C", "#5C4910", "#275A38", "#3D3B39" };
    public static readonly string[] StrokeLight = { "#F1B0B7", "#FFE28A", "#A3D9B8", "#E0DFDD" };
}

sealed class PoFulfillmentColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var s = PoFulfil.Classify(value);
        return BoardTheme.Brush(BoardTheme.IsDark ? PoFulfil.TextDark[s] : PoFulfil.TextLight[s]);
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class PoFulfillmentBgConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var s = PoFulfil.Classify(value);
        return BoardTheme.Brush(BoardTheme.IsDark ? PoFulfil.BgDark[s] : PoFulfil.BgLight[s]);
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class PoFulfillmentStrokeConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var s = PoFulfil.Classify(value);
        return BoardTheme.Brush(BoardTheme.IsDark ? PoFulfil.StrokeDark[s] : PoFulfil.StrokeLight[s]);
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class ApprovalStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var text = value as string ?? "";
        if (text.StartsWith("Received", StringComparison.OrdinalIgnoreCase) || text.Contains("Signed", StringComparison.OrdinalIgnoreCase))
            return BoardTheme.Pick("#6CCB5F", "#107C41");
        if (text.StartsWith("Sent", StringComparison.OrdinalIgnoreCase) || text.Contains("Pending", StringComparison.OrdinalIgnoreCase))
            return BoardTheme.Pick("#FFC83B", "#8A5700");
        return BoardTheme.Pick("#9E9E9E", "#757575");
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

// {Binding X, Converter={StaticResource Fmt}, ConverterParameter='Shared ({0})'}
sealed class FormatConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        string.Format(p as string ?? "{0}", value);
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class DateTimeOffsetNullConverter : IValueConverter
{
    public object? Convert(object value, Type t, object p, string l) =>
        value is DateTime d && d != default ? new DateTimeOffset(d) : (DateTimeOffset?)null;
    public object? ConvertBack(object value, Type t, object p, string l) =>
        value is DateTimeOffset o ? o.DateTime : default(DateTime);
}

// int/collection count == 0 -> Visible (for empty-state text)
sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var n = value switch { int i => i, System.Collections.ICollection c => c.Count, _ => 1 };
        return n == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

// int/collection count > 0 -> Visible
sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var n = value switch { int i => i, System.Collections.ICollection c => c.Count, _ => 0 };
        return n > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class SelectAllToggleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) => value is true ? "Deselect All" : "Select All";
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}
