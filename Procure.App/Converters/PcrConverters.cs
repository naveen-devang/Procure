using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Procure.App.Converters;

/// <summary>PNG bytes (one rasterized PCR page) -> BitmapImage. The rasterizer hands the
/// preview a fresh byte[] per page; WinUI's Image wants an ImageSource.</summary>
sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object value, Type t, object p, string l)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        var img = new BitmapImage();
        _ = SetAsync(img, bytes);
        return img;
    }

    private static async System.Threading.Tasks.Task SetAsync(BitmapImage img, byte[] bytes)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await img.SetSourceAsync(stream);
        }
        catch { /* a torn-down page image just stays blank */ }
    }

    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

/// <summary>value == ConverterParameter -> accent Background for a segmented-choice button,
/// else the subtle fill. Replaces the MAUI border-width-on-equality trick.</summary>
/// <summary>Foreground for a segmented pill, paired with SegmentedSelectionBrushConverter: the
/// selected one is filled with the accent, so its label has to be the on-accent colour or it
/// disappears into the fill.</summary>
sealed class SegmentedSelectionTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] as Brush
              ?? new SolidColorBrush(Microsoft.UI.Colors.White)
            : Application.Current.Resources["AppTextPrimary"] as Brush
              ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

sealed class SegmentedSelectionBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
              ?? new SolidColorBrush(Microsoft.UI.Colors.SteelBlue)
            : Application.Current.Resources["AppSubtleFill"] as Brush
              ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}
