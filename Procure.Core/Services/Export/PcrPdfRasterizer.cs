using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Pdf;
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

        /// <summary>The sharpest a print is rendered: past this an office printer adds nothing.</summary>
        public const double MaxPrintDpi = 600;

        /// <summary>
        /// The DPI a print is rendered at, sized to the smallest text on the sheet instead of fixed.
        ///
        /// A sheet's body text is 7.5 pt, which gets ~31 dots per em at 300 DPI and prints crisp. A
        /// sheet fitted to the page for many suppliers prints that text smaller - 12 suppliers on A4
        /// landscape come out near 4.9 pt, only ~20 dots at 300 DPI, which reads fuzzy on a 600 DPI
        /// laser. So the smallest text gets at least ~25 dots per em: 300 DPI (what every normal sheet
        /// already uses, so it costs exactly what it did) down to 6 pt text - about eight suppliers on
        /// A4 - and 600 below that, never above what the printer itself says it prints at.
        /// </summary>
        /// <param name="smallestPrintedPt">The sheet's body text size after any fit scaling.</param>
        /// <param name="printerDpi">The printer's reported resolution; 0 or less when unknown.</param>
        public static double PrintDpiFor(double smallestPrintedPt, double printerDpi)
        {
            const double dotsPerEm = 25;
            var needed = smallestPrintedPt > 0 ? dotsPerEm * 72.0 / smallestPrintedPt : PrintDpi;
            // Two steps only - what printers actually print at - so a sheet is either exactly as it was or sharp.
            var dpi = needed <= PrintDpi ? PrintDpi : MaxPrintDpi;
            return printerDpi > 0 ? Math.Min(dpi, Math.Max(PrintDpi, printerDpi)) : dpi;
        }

        // A0 at 300 DPI is 14042x9933 - a 558MB decoded bitmap that GDI+ will simply fail on, and
        // the paper dropdown does offer A0. Capping the longest rendered edge lets outsized paper
        // degrade to a lower DPI instead of falling over. A4 and A3 - what a PCR is realistically
        // printed on - stay at the full requested DPI (A3 at 300 DPI is 4963px on its long edge).
        // Raised from 5000 so A4 still gets the full MaxPrintDpi (7016 px on its long edge). Only one
        // page is ever held at that size - printing renders a page when the printer asks for it.
        public const double MaxRenderEdgePx = 7100;

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
        /// returned value rather than the one they asked for - the two differ on outsized paper.
        ///
        /// Used by printing, which needs every page. The preview does not - it opens a
        /// <see cref="PcrPdfPageSource"/> and asks for pages as they are needed.</summary>
        public static async Task<(List<byte[]> Pages, double Dpi)> RenderPagesAsync(byte[] pdfBytes, double dpi = DefaultDpi)
        {
            var source = await PcrPdfPageSource.OpenAsync(pdfBytes, dpi);
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

    /// <summary>
    /// An opened PDF that renders one page at a time, on request.
    ///
    /// The preview used to rasterize every page before showing the first, and did it again on every
    /// option change - paper size, orientation, margins, shrink-to-fit. A six-page comparison paid six
    /// full-resolution renders before anything appeared. Opening the document once and rendering on
    /// demand lets page one reach the screen while the rest are still being drawn.
    ///
    /// The document is parsed once and reused for every page; PdfDocument is an agile WinRT object, so
    /// the renders can run on whatever thread the caller is on.
    /// </summary>
    public sealed class PcrPdfPageSource
    {
        private readonly PdfDocument _document;
        private readonly double _scale;

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

        /// <summary>A page's paper size in inches.</summary>
        public (double Width, double Height) PageSizeInches(int index)
        {
            using var page = _document.GetPage((uint)index);
            return (page.Size.Width / 96.0, page.Size.Height / 96.0);
        }

        /// <summary>A page's size in device-independent pixels (96 per inch).</summary>
        public (double Width, double Height) PageSizeDips(int index)
        {
            using var page = _document.GetPage((uint)index);
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
            using var page = _document.GetPage((uint)index);
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
            using var page = _document.GetPage((uint)index);
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
    }
}
