using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Procure.Services.Export
{
    // Rasterizes the exporter's own PDF bytes into page images (PNG) for the preview modal and for
    // printing. Windows.Data.Pdf is a first-party renderer, so nothing else has to understand the
    // PDF byte format PcrPdfExporter hand-writes.
    public static class PcrPdfRasterizer
    {
        /// <summary>On-screen preview. Plenty for a ~96 DPI display, and small enough that a
        /// multi-page preview stays cheap to hold.</summary>
        public const double DefaultDpi = 130;

        /// <summary>Printing. 130 DPI lands roughly 4x under a 600 DPI printer, which reads as soft
        /// text and uneven table rules next to Acrobat printing the same PDF as vectors.</summary>
        public const double PrintDpi = 300;

        /// <summary>The resolution a print is rendered at: past this an office printer adds nothing.</summary>
        public const double MaxPrintDpi = 600;

        /// <summary>
        /// The DPI a print is rendered at: <see cref="MaxPrintDpi"/>, or the printer's own resolution
        /// when that is lower.
        ///
        /// Prints go out black and white (<see cref="PcrPdfPageSource.RenderMonochromeAsync"/>), and at
        /// one bit per pixel 600 DPI is both the sharpest and the lightest option there is. Measured on
        /// an A4 landscape page as GDI+ records it for the spooler: 300 DPI colour 17.1 MB, 600 DPI
        /// colour 33.7 MB, 600 DPI 8-bit grey 33.5 MB, 600 DPI black and white 4.3 MB. So there is no
        /// saving to be had from printing a normal sheet at a lower resolution, and small text on a
        /// sheet fitted for many suppliers gets every dot the printer has.
        /// </summary>
        /// <param name="printerDpi">The printer's reported resolution; 0 or less when unknown.</param>
        public static double PrintDpiFor(double printerDpi)
            => printerDpi > 0 ? Math.Min(MaxPrintDpi, Math.Max(PrintDpi, printerDpi)) : MaxPrintDpi;

        // A0 at 300 DPI is 14042x9933 - a 558MB decoded bitmap that GDI+ will simply fail on, and
        // the paper dropdown does offer A0. Capping the longest rendered edge lets outsized paper
        // degrade to a lower DPI instead of falling over. 10000 px lets A3 - the largest paper a PCR is
        // realistically printed on - reach the full 600 DPI (9921 px). Printing never holds a colour
        // page at that size: it renders in strips into a one-bit page (4.3 MB for A4, 8.7 MB for A3).
        public const double MaxRenderEdgePx = 10000;

        // PdfPage.Size is in device-independent pixels (96 per inch), NOT PDF points (72 per inch).
        // Scaling it by dpi/72 - as this did - rendered every page 4/3 oversized: A4 landscape came
        // out 2027x1432 where 1520x1075 is correct. Harmless-looking in the preview, which just got
        // bigger images than it asked for, but the print path derives the sheet's physical size by
        // dividing those pixels by the DPI, so it asked the driver for a 15.6x11in sheet instead of
        // A4. No printer has that paper, so the driver substituted its own and rescaled - which is
        // the print-does-not-match-the-preview bug. Acrobat was unaffected because it reads the
        // PDF's MediaBox, which PcrPdfExporter writes correctly as [0 0 842 595].
        private const double DipsPerInch = 96.0;

        /// <summary>Renders every page, and reports the DPI it actually used. Callers reconstruct a
        /// page's physical size by dividing its pixel dimensions by that number, so they must use the
        /// returned value rather than the one they asked for - the two differ on outsized paper.</summary>
        public static async Task<(List<byte[]> Pages, double Dpi)> RenderPagesAsync(byte[] pdfBytes, double dpi = DefaultDpi)
        {
            using var source = await PcrPdfPageSource.OpenAsync(pdfBytes, dpi);
            var images = new List<byte[]>(source.PageCount);
            for (var i = 0; i < source.PageCount; i++)
                images.Add(await source.RenderAsync(i));
            return (images, source.EffectiveDpi);
        }

        /// <summary>The DPI a page of this DIP size is rendered at: the requested figure, pulled down
        /// only as far as needed to keep the longest edge within <see cref="MaxRenderEdgePx"/>.
        /// Pure, so PrintGeometrySelfCheck can pin the cap without rendering anything.</summary>
        internal static double EffectiveDpi(double widthDips, double heightDips, double requestedDpi)
        {
            var longestIn = Math.Max(widthDips, heightDips) / DipsPerInch;
            return longestIn > 0 ? Math.Min(requestedDpi, MaxRenderEdgePx / longestIn) : requestedDpi;
        }
    }

    /// <summary>A page as one bit per pixel, top row first, most significant bit leftmost, 1 = white
    /// (the default two-colour palette of a GDI 1bpp bitmap). Rows are padded to 4 bytes.</summary>
    public sealed record MonochromePage(byte[] Bits, int Width, int Height, int Stride, double Dpi);

    /// <summary>
    /// An opened PDF that renders one page at a time, on request.
    ///
    /// The preview used to rasterize every page before showing the first, and did it again on every
    /// option change - paper size, orientation, margins, shrink-to-fit. A six-page comparison paid six
    /// full-resolution renders before anything appeared. Opening the document once and rendering on
    /// demand lets page one reach the screen while the rest are still being drawn.
    ///
    /// The document is parsed once and reused for every page; PdfDocument is an agile WinRT object, so
    /// the renders can run on whatever thread the caller is on. Dispose it when done: the preview
    /// replaces its document on every option change, and an undisposed one stays in memory until a
    /// garbage collection happens to reach it.
    /// </summary>
    public sealed class PcrPdfPageSource : IDisposable
    {
        private readonly PdfDocument _document;
        private readonly double _scale;
        private bool _disposed;

        public int PageCount { get; }

        /// <summary>The DPI pages actually render at - see PcrPdfRasterizer.RenderPagesAsync.</summary>
        public double EffectiveDpi { get; }

        private PcrPdfPageSource(PdfDocument document, double effectiveDpi)
        {
            _document = document;
            PageCount = (int)document.PageCount;
            EffectiveDpi = effectiveDpi;
            _scale = effectiveDpi / 96.0;
        }

        public static async Task<PcrPdfPageSource> OpenAsync(byte[] pdfBytes, double dpi = PcrPdfRasterizer.DefaultDpi)
        {
            using var inputStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(inputStream))
            {
                writer.WriteBytes(pdfBytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            inputStream.Seek(0);

            var document = await PdfDocument.LoadFromStreamAsync(inputStream);

            // One DPI for the whole document, decided by page 0: every page of a PCR is the same
            // size, and callers derive physical page size from pixels, so a per-page DPI would
            // silently describe some pages as a different paper than others.
            var effectiveDpi = dpi;
            if (document.PageCount > 0)
            {
                using var firstPage = document.GetPage(0);
                effectiveDpi = PcrPdfRasterizer.EffectiveDpi(firstPage.Size.Width, firstPage.Size.Height, dpi);
            }

            return new PcrPdfPageSource(document, effectiveDpi);
        }

        public bool IsDisposed => _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // PdfDocument has no Close/Dispose of its own; releasing the WinRT reference is what frees
            // the parsed document now rather than whenever the finalizer gets to it.
            if ((object)_document is WinRT.IWinRTObject winrt) winrt.NativeObject?.Dispose();
        }

        private PdfPage Page(int index)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _document.GetPage((uint)index);
        }

        /// <summary>A page's paper size in inches.</summary>
        public (double Width, double Height) PageSizeInches(int index)
        {
            using var page = Page(index);
            return (page.Size.Width / 96.0, page.Size.Height / 96.0);
        }

        /// <summary>A page's size in device-independent pixels (96 per inch).</summary>
        public (double Width, double Height) PageSizeDips(int index)
        {
            using var page = Page(index);
            return (page.Size.Width, page.Size.Height);
        }

        /// <summary>
        /// Part of a page, drawn straight from the PDF at exactly the pixel size asked for, as a PNG
        /// stream. The preview uses it when zoomed in past what its page image holds: only the visible
        /// part is drawn, at the screen's own resolution, so it is sharp at any zoom while costing no
        /// more than one viewport of pixels however far in it goes.
        /// </summary>
        /// <param name="sourceDips">The region of the page, in the page's DIPs.</param>
        public async Task<IRandomAccessStream> RenderRegionAsync(int index, Windows.Foundation.Rect sourceDips, uint widthPx, uint heightPx)
        {
            using var page = Page(index);
            var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                SourceRect = sourceDips,
                DestinationWidth = Math.Max(1u, widthPx),
                DestinationHeight = Math.Max(1u, heightPx)
            });
            stream.Seek(0);
            return stream;
        }

        /// <summary>One page as PNG bytes.</summary>
        public async Task<byte[]> RenderAsync(int index)
        {
            using var page = Page(index);
            using var pageStream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(pageStream, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Round(page.Size.Width * _scale),
                DestinationHeight = (uint)Math.Round(page.Size.Height * _scale)
            });

            pageStream.Seek(0);
            using var reader = new DataReader(pageStream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)pageStream.Size);
            var bytes = new byte[pageStream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }

        // Colour pixels rendered per strip. Printing never holds more than this much colour at once,
        // however large the page: 4 million pixels is 16 MB.
        private const int StripPixelBudget = 4_000_000;

        // 4x4 ordered-dither thresholds. Black stays black and white stays white; a light fill like the
        // table heading's shading becomes a sparse even dot pattern - what a laser prints for it anyway -
        // instead of vanishing (a flat threshold) or turning to noise (error diffusion).
        private static readonly byte[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        /// <summary>
        /// A page for printing: black and white, one bit per pixel, at this source's DPI.
        ///
        /// Rendered from the PDF in horizontal strips, each converted to black and white as soon as it
        /// is drawn, so no full colour page ever exists: A3 at 600 DPI would be a 278 MB colour bitmap,
        /// but is held here as 8.7 MB.
        /// </summary>
        public async Task<MonochromePage> RenderMonochromeAsync(int index)
        {
            var (pageWidthDips, pageHeightDips) = PageSizeDips(index);
            int width = (int)Math.Round(pageWidthDips * _scale);
            int height = (int)Math.Round(pageHeightDips * _scale);
            int stride = ((width + 31) / 32) * 4;
            var bits = new byte[stride * height];

            int stripRows = Math.Max(1, StripPixelBudget / Math.Max(1, width));
            double dipsPerRow = pageHeightDips / height;

            for (int top = 0; top < height; top += stripRows)
            {
                int rows = Math.Min(stripRows, height - top);
                using var strip = await RenderRegionAsync(index,
                    new Windows.Foundation.Rect(0, top * dipsPerRow, pageWidthDips, rows * dipsPerRow),
                    (uint)width, (uint)rows);

                var decoder = await BitmapDecoder.CreateAsync(strip);
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                var bgra = pixels.DetachPixelData();

                int stripWidth = (int)decoder.PixelWidth;
                for (int y = 0; y < rows && y < decoder.PixelHeight; y++)
                {
                    int src = y * stripWidth * 4;
                    int dst = (top + y) * stride;
                    int row = top + y;
                    for (int x = 0; x < width && x < stripWidth; x++, src += 4)
                    {
                        int grey = (bgra[src] * 29 + bgra[src + 1] * 150 + bgra[src + 2] * 77) >> 8;
                        int threshold = Bayer4[((row & 3) << 2) | (x & 3)] * 16 + 8;
                        if (grey > threshold) bits[dst + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }
                }
            }

            return new MonochromePage(bits, width, height, stride, EffectiveDpi);
        }
    }
}
