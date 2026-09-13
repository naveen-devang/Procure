using System;
using System.Globalization;
using System.IO;
using Microsoft.Maui.Controls;

namespace Procure.Utilities
{
    /// <summary>byte[] (a PNG) -> ImageSource, for the PCR preview pages. The view model holds
    /// raw bytes so it can live in Procure.Core; the image type is bound here at the view.</summary>
    public sealed class BytesToImageSourceConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is byte[] { Length: > 0 } bytes)
                return ImageSource.FromStream(() => new MemoryStream(bytes));
            return null;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
