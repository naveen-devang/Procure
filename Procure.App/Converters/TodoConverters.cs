using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Procure.Models;

namespace Procure.App.Converters;

// WinUI ports of Utilities/TodoConverters.cs. Brushes, so they bind straight to Foreground/Background.

sealed class TodoPriorityColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) => value switch
    {
        TodoPriority.High => BoardTheme.Pick("#FF9385", "#F2604E"),
        TodoPriority.Medium => BoardTheme.Pick("#FFC062", "#F0A526"),
        TodoPriority.Low => BoardTheme.Pick("#7FDCAB", "#39B87A"),
        _ => BoardTheme.Pick("#9A9A9A", "#CFCFCF"),
    };
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class TodoOverdueColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        value is true ? BoardTheme.Pick("#E0796D", "#B5493F") : BoardTheme.Pick("#A9B0BD", "#605E5C");
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

// Segment toggle: value is bool (view toggle) or enum + string ConverterParameter (priority picker).
sealed class TodoSegmentBgConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var active = value is bool b ? b
            : string.Equals(value?.ToString(), p as string, StringComparison.OrdinalIgnoreCase);
        return active ? BoardTheme.Pick("#3A3A3C", "#FFFFFF")
                      : (Brush)new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class TodoSegmentTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        var active = value is bool b ? b
            : string.Equals(value?.ToString(), p as string, StringComparison.OrdinalIgnoreCase);
        return active ? BoardTheme.Pick("#F3F2F1", "#1A1A1A") : BoardTheme.Pick("#9A9A9A", "#6B6B6B");
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class StrikethroughConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        value is true ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class DoneGlyphConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) => value is true ? "" : "";
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

// DateTime <-> DateTimeOffset for CalendarDatePicker.Date
sealed class DateTimeOffsetConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        value is DateTime d && d != default ? new DateTimeOffset(d) : null;
    public object ConvertBack(object value, Type t, object p, string l) =>
        value is DateTimeOffset o ? o.DateTime : default(DateTime);
}
