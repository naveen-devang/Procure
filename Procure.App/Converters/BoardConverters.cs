using System;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Procure.Models;

namespace Procure.App.Converters;

/// <summary>
/// WinUI ports of the MAUI board converters (Utilities/StatusColorConverter.cs,
/// PriorityColorConverter.cs). WinUI can't bind a Color to a Brush property, so these
/// return SolidColorBrush. Theme-varying pairs read <see cref="BoardTheme.IsDark"/>,
/// which MainWindow keeps current and bumps on theme switch (the board rebuilds its
/// item source then, since converter bindings don't re-run on RequestedTheme change).
/// </summary>
public static class BoardTheme
{
    public static bool IsDark { get; set; }

    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new();

    public static SolidColorBrush Brush(string hex) => Cache.GetOrAdd(hex, static s =>
    {
        s = s.TrimStart('#');
        byte a = 255;
        if (s.Length == 8) { a = Convert.ToByte(s[..2], 16); s = s[2..]; }
        return new SolidColorBrush(Color.FromArgb(a,
            Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16)));
    });

    public static SolidColorBrush Pick(string dark, string light) => Brush(IsDark ? dark : light);
}

abstract class BrushConverter : IValueConverter
{
    protected abstract SolidColorBrush Map(object? value, object? parameter);
    public object Convert(object value, Type targetType, object parameter, string language) => Map(value, parameter);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

sealed class StatusColorConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) => (v as string) switch
    {
        ProcurementStatus.Delivered or ProcurementStatus.Closed or "Signed" or "True" => BoardTheme.Pick("#6CCB5F", "#107C41"),
        ProcurementStatus.PcrApproved => BoardTheme.Pick("#60CDFF", "#004E8C"),
        ProcurementStatus.PoRaised or ProcurementStatus.PartiallyDelivered => BoardTheme.Pick("#FCE100", "#7A4B04"),
        ProcurementStatus.PcrSubmitted or ProcurementStatus.QuotesReceived => BoardTheme.Pick("#C4A6FE", "#4B2BA8"),
        ProcurementStatus.RfqSent => BoardTheme.Pick("#4DD0E1", "#005A70"),
        ProcurementStatus.PrRaised => BoardTheme.Pick("#D2D0CE", "#494847"),
        ProcurementStatus.OnHold => BoardTheme.Pick("#FFA043", "#8A3B00"),
        ProcurementStatus.Cancelled => BoardTheme.Pick("#FF99A4", "#A80000"),
        ProcurementStatus.Merged => BoardTheme.Pick("#C8C6C4", "#494847"),
        _ => BoardTheme.Pick("#D2D0CE", "#494847"),
    };
}

sealed class StatusBadgeBgConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) => (v as string) switch
    {
        ProcurementStatus.Delivered or ProcurementStatus.Closed or "Signed" or "True" => BoardTheme.Pick("#133824", "#E7F3ED"),
        ProcurementStatus.PcrApproved => BoardTheme.Pick("#142F4C", "#EBF3FC"),
        ProcurementStatus.PoRaised or ProcurementStatus.PartiallyDelivered => BoardTheme.Pick("#3B2E08", "#FFF4CE"),
        ProcurementStatus.PcrSubmitted or ProcurementStatus.QuotesReceived => BoardTheme.Pick("#281A4C", "#F0EBF9"),
        ProcurementStatus.RfqSent => BoardTheme.Pick("#103B44", "#E0F7FA"),
        ProcurementStatus.PrRaised => BoardTheme.Pick("#2D2C2C", "#F3F2F1"),
        ProcurementStatus.OnHold => BoardTheme.Pick("#3D2008", "#FFF0E6"),
        ProcurementStatus.Cancelled => BoardTheme.Pick("#3F1011", "#FDE7E9"),
        ProcurementStatus.Merged => BoardTheme.Pick("#2D2C2C", "#EDEBE9"),
        _ => BoardTheme.Pick("#2D2C2C", "#F3F2F1"),
    };
}

sealed class PriorityColorConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) =>
        string.Equals(v as string, ProcurementPriority.Urgent, StringComparison.OrdinalIgnoreCase)
            ? BoardTheme.Pick("#FF99A4", "#A80000") : BoardTheme.Pick("#D2D0CE", "#494847");
}

sealed class PriorityBgConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) =>
        string.Equals(v as string, ProcurementPriority.Urgent, StringComparison.OrdinalIgnoreCase)
            ? BoardTheme.Pick("#3F1011", "#FDE7E9") : BoardTheme.Pick("#2D2C2C", "#F3F2F1");
}

sealed class PriorityBorderConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) =>
        string.Equals(v as string, ProcurementPriority.Urgent, StringComparison.OrdinalIgnoreCase)
            ? BoardTheme.Pick("#5C1A1C", "#F1B0B7") : BoardTheme.Pick("#3D3B39", "#E0DFDD");
}

sealed class PoPillTextColorConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) =>
        v is decimal d && d > 0 ? BoardTheme.Pick("#6CCB5F", "#107C41") : BoardTheme.Pick("#A19F9D", "#605E5C");
}

sealed class PcrPillTextColorConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p)
    {
        var t = v as string ?? "";
        if (t.Contains("Pending", StringComparison.OrdinalIgnoreCase)) return BoardTheme.Pick("#FFC83B", "#8A5700");
        if (t.Contains("Approved", StringComparison.OrdinalIgnoreCase)) return BoardTheme.Pick("#6CCB5F", "#107C41");
        return BoardTheme.Pick("#A19F9D", "#605E5C");
    }
}

sealed class PrTypeColorConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) => (v as string) switch
    {
        ProcurementPrType.StoresAndSpares => BoardTheme.Pick("#4DD0E1", "#00687A"),
        ProcurementPrType.RawMaterial => BoardTheme.Pick("#FFB968", "#8A3B00"),
        ProcurementPrType.PackingMaterial => BoardTheme.Pick("#52BE80", "#0B8A5A"),
        ProcurementPrType.Service => BoardTheme.Pick("#D7BDE2", "#7D3C98"),
        ProcurementPrType.Capex => BoardTheme.Pick("#F1948A", "#A93226"),
        _ => BoardTheme.Pick("#D2D0CE", "#494847"),
    };
}

sealed class PrTypeBgConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) => (v as string) switch
    {
        ProcurementPrType.StoresAndSpares => BoardTheme.Pick("#002D38", "#E0F7FA"),
        ProcurementPrType.RawMaterial => BoardTheme.Pick("#3B2200", "#FFF4CE"),
        ProcurementPrType.PackingMaterial => BoardTheme.Pick("#0B2F25", "#E8F8F5"),
        ProcurementPrType.Service => BoardTheme.Pick("#2E1437", "#F4ECF7"),
        ProcurementPrType.Capex => BoardTheme.Pick("#3D1414", "#FDEDEC"),
        _ => BoardTheme.Pick("#2D2C2C", "#F3F2F1"),
    };
}

sealed class PrTypeBorderConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) => (v as string) switch
    {
        ProcurementPrType.StoresAndSpares => BoardTheme.Pick("#006073", "#80DEEA"),
        ProcurementPrType.RawMaterial => BoardTheme.Pick("#663C00", "#FFE082"),
        ProcurementPrType.PackingMaterial => BoardTheme.Pick("#196F3D", "#A3E4D7"),
        ProcurementPrType.Service => BoardTheme.Pick("#6C3483", "#D2B4DE"),
        ProcurementPrType.Capex => BoardTheme.Pick("#78281F", "#F5B7B1"),
        _ => BoardTheme.Pick("#3D3B39", "#E0DFDD"),
    };
}

sealed class BoolToFilterChipBgConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) =>
        v is true ? BoardTheme.Pick("#142F4C", "#EBF3FC") : BoardTheme.Pick("#2B2B2B", "#FFFFFF");
}

sealed class BoolToFilterChipStrokeConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) =>
        v is true ? BoardTheme.Pick("#60CDFF", "#0078D4") : BoardTheme.Pick("#404040", "#B0B0B0");
}

sealed class BoolToFilterChipTextConverter : BrushConverter
{
    protected override SolidColorBrush Map(object? v, object? p) =>
        v is true ? BoardTheme.Pick("#60CDFF", "#004E8C") : BoardTheme.Pick("#FFFFFF", "#1A1A1A");
}

// --- non-brush helpers ---

sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var visible = value is string s ? !string.IsNullOrWhiteSpace(s) : value != null;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var b = value is true;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class BoolToChevronConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) => value is true ? "\uE70E" : "\uE70D";
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}
