using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Procure.Models;

namespace Procure.Services.Export
{
    public class PcrExportService : IPcrExportService
    {
        public async Task<string> ExportPcrToExcelAsync(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks)
        {
            var bytes = PcrExcelExporter.GenerateExcel(pr, pcr, selectedRfqs, remarks);
            var safePrNo = string.IsNullOrWhiteSpace(pr.PrNo) ? "PR" : pr.PrNo.Replace("/", "-").Replace("\\", "-");
            var filename = $"PriceComparison_{safePrNo}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return await SaveAndOpenFileAsync(bytes, filename);
        }

        public byte[] GeneratePcrPdfBytes(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks,
            PcrPdfOptions options)
            => PcrPdfExporter.GeneratePdf(pr, pcr, selectedRfqs, remarks, options);

        public async Task<string?> SavePcrPdfAsync(byte[] pdfBytes, string suggestedFileName)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(GetActiveWindow());

            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop
            };
            picker.FileTypeChoices.Add("PDF Document", new List<string> { ".pdf" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return null;

            await Windows.Storage.FileIO.WriteBytesAsync(file, pdfBytes);

            try
            {
                await Launcher.Default.OpenAsync(new OpenFileRequest
                {
                    Title = file.Name,
                    File = new ReadOnlyFile(file.Path)
                });
            }
            catch
            {
                // Non-fatal if no viewer is available.
            }

            return file.Path;
        }

        // Printer names only, via classic Windows print-spooler enumeration — no window handle or
        // WinRT interop needed, unlike the file pickers above.
        public IReadOnlyList<string> GetAvailablePrinters()
            => System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().ToList();

        public string GetDefaultPrinterName()
            => new System.Drawing.Printing.PrinterSettings().PrinterName;

        public async Task<bool> PrintPcrPdfAsync(byte[] pdfBytes, string printerName, string jobTitle, bool doubleSided, IReadOnlyList<int>? pageIndices, int copies = 1)
        {
            // A "printer" that's actually a PDF/XPS writer (Microsoft Print to PDF, Adobe PDF, ...)
            // shows its own Save-As dialog after the job is spooled - and GDI's PrintDocument.Print()
            // returns successfully either way, whether the user went through with that dialog or
            // cancelled it. There is no reliable signal back from that dialog through the print
            // pipeline: spooler job status was tried and proved too racy (a cancelled job can leave
            // the queue before it's ever observed as failed, indistinguishable from a fast success).
            // Since what these "printers" actually do IS save a file, route them through the exact
            // file-save flow used by Save As instead, which already reports cancellation correctly.
            // Duplex/copies don't carry over to this path - neither applies to a saved file. The page
            // range does: the caller (see IsFileWriterPrinter) hands over a PDF already cut to it.
            if (IsFileWriterPrinter(printerName))
            {
                var safeName = string.Concat(jobTitle.Split(Path.GetInvalidFileNameChars()));
                var savedPath = await SavePcrPdfAsync(pdfBytes, $"{safeName}.pdf");
                return savedPath != null;
            }

            // Print renders sharper than the preview - see PcrPdfRasterizer.PrintDpi. The DPI comes
            // back with the pages because outsized paper is rendered below the requested figure, and
            // the paper-size maths below divides by it.
            var (allPages, renderDpi) = await PcrPdfRasterizer.RenderPagesAsync(pdfBytes, PcrPdfRasterizer.PrintDpi);
            if (allPages.Count == 0) return true;

            // Empty/null selection means "every page" - the common case, and what the Adobe-style
            // page-range box in the preview defaults to when left blank.
            var selectedPages = (pageIndices == null || pageIndices.Count == 0)
                ? Enumerable.Range(0, allPages.Count).ToList()
                : pageIndices.Where(i => i >= 0 && i < allPages.Count).Distinct().OrderBy(i => i).ToList();
            if (selectedPages.Count == 0) selectedPages = Enumerable.Range(0, allPages.Count).ToList();

            return await Task.Run(() =>
            {
                using var printDocument = new System.Drawing.Printing.PrintDocument();
                if (!string.IsNullOrWhiteSpace(printerName))
                {
                    printDocument.PrinterSettings.PrinterName = printerName;
                }
                printDocument.DocumentName = jobTitle;

                // PrintDocument defaults to Portrait/whatever paper the driver defaults to,
                // regardless of what's actually being drawn - previously nothing here ever told it
                // otherwise, so a landscape PCR sheet was sent as a Portrait job: the printer fed
                // and marked the physical page as Portrait while the (correctly landscape) bitmap
                // got squeezed to fit that narrow Portrait MarginBounds, which is why the printout
                // needed rotating to read and looked scaled differently than the preview.
                // The rasterized bitmap already carries this job's real geometry - PcrPdfExporter's
                // chosen orientation and paper size, baked in at render time - so reading it back
                // from the first selected page's own pixel dimensions (at the same DPI the
                // rasterizer used) gives an accurate page description without this method needing
                // to know about PcrPdfOptions at all.
                bool isLandscapeJob;
                using (var firstImage = System.Drawing.Image.FromStream(new MemoryStream(allPages[selectedPages[0]])))
                {
                    bool isLandscape = isLandscapeJob = firstImage.Width > firstImage.Height;
                    double widthIn = firstImage.Width / renderDpi;
                    double heightIn = firstImage.Height / renderDpi;

                    // PaperSize.Width/Height are always the sheet's un-rotated (portrait-convention)
                    // dimensions - Landscape is what actually flips how it's fed/measured. Storing
                    // the already-landscape pixel size directly here would make GDI swap it a
                    // second time when Landscape is applied.
                    var (paperWidthIn, paperHeightIn) = isLandscape ? (heightIn, widthIn) : (widthIn, heightIn);

                    printDocument.DefaultPageSettings.Landscape = isLandscape;
                    printDocument.DefaultPageSettings.PaperSize = ResolvePaperSize(
                        printDocument.PrinterSettings,
                        (int)Math.Round(paperWidthIn * 100),
                        (int)Math.Round(paperHeightIn * 100));
                }

                // Copies go to the driver as DEVMODE dmCopies, collated - the same thing Word's
                // Copies box does. Clamped to what the driver says it can do; MaximumCopies has been
                // seen to throw on virtual printers, same as CanDuplex below, hence the guard.
                int maxCopies = short.MaxValue;
                try { maxCopies = printDocument.PrinterSettings.MaximumCopies; } catch { /* keep the request */ }
                printDocument.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, Math.Max(1, maxCopies));
                printDocument.PrinterSettings.Collate = true;

                // CanDuplex queries the driver and has been seen to throw on some virtual/PDF
                // printers rather than just returning false - guarded so a duplex request never
                // blocks the actual print, it just silently prints one-sided instead.
                var canDuplex = false;
                try { canDuplex = printDocument.PrinterSettings.CanDuplex; } catch { /* treat as no duplex support */ }

                // Vertical/Horizontal here mean flip-on-long-edge / flip-on-short-edge of the
                // physical sheet, not "portrait/landscape" content - a landscape job (this sheet's
                // default) needs the short-edge flip to come out right-side-up on the back; using
                // the long-edge flip unconditionally left double-sided landscape printouts upside
                // down on every other page.
                printDocument.PrinterSettings.Duplex = doubleSided && canDuplex
                    ? (isLandscapeJob ? System.Drawing.Printing.Duplex.Horizontal : System.Drawing.Printing.Duplex.Vertical)
                    : System.Drawing.Printing.Duplex.Simplex;

                var cursor = 0;
                printDocument.PrintPage += (_, e) =>
                {
                    using var pageStream = new MemoryStream(allPages[selectedPages[cursor]]);
                    using var image = System.Drawing.Image.FromStream(pageStream);

                    // Fit the rasterized page into the PRINTABLE region, preserving aspect ratio, so
                    // the printed sheet matches the on-screen preview. Not MarginBounds - the bitmap
                    // already has its own margin preset (Normal/Narrow/Wide) baked in from
                    // PcrPdfExporter, so fitting inside MarginBounds stacks a second margin on top.
                    // And not PageBounds either, which is what this used to do: PageBounds is the
                    // whole physical sheet, but the Graphics origin here is the printable corner
                    // (OriginAtMargins is false), so scaling to sheet size and drawing from there
                    // pushed the right and bottom edges into the printer's unprintable band -
                    // measured on a real driver at A4 landscape, sheet 1169x827 against a printable
                    // 1129.7x793.3, losing 0.39in off the right and 0.33in off the bottom. That is a
                    // cut-off vendor column and signature row on a Narrow-margin sheet.
                    //
                    // VisibleClipBounds is that region already expressed in this coordinate space
                    // and already swapped for landscape - unlike PageSettings.PrintableArea, which
                    // stays portrait-shaped even when Landscape is set (measured: 850x1100 for a
                    // landscape page whose Bounds had rotated to 1100x850).
                    var dest = PlacePage(e.Graphics!, image.Width, image.Height);

                    // Default interpolation visibly softens a raster scaled onto a 600+ DPI device
                    // and makes the table rules uneven; costs nothing at one image per page.
                    e.Graphics!.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    e.Graphics.DrawImage(image, dest);

                    cursor++;
                    e.HasMorePages = cursor < selectedPages.Count;
                };

                printDocument.Print();

                // A real printer prints silently through this path - there's no user-facing dialog
                // for it to cancel, so returning without an exception here really does mean it went
                // to the print queue.
                return true;
            });
        }

        /// <summary>Where a rasterized page of this pixel size goes on the sheet. The production
        /// placement decision, named so PrintGeometrySelfCheck can call it with a real driver's
        /// measurement Graphics and prove the print path uses the printable region - asserting on
        /// FitInto alone would only prove the arithmetic, not which box gets handed to it.</summary>
        internal static System.Drawing.RectangleF PlacePage(System.Drawing.Graphics g, int imageWidth, int imageHeight)
            => FitInto(imageWidth, imageHeight, g.VisibleClipBounds);

        /// <summary>Largest aspect-preserving rectangle for an image of this pixel size, centred
        /// inside <paramref name="area"/>.</summary>
        internal static System.Drawing.RectangleF FitInto(int imageWidth, int imageHeight, System.Drawing.RectangleF area)
        {
            if (imageWidth <= 0 || imageHeight <= 0) return area;

            var scale = Math.Min(area.Width / imageWidth, area.Height / imageHeight);
            var width = imageWidth * scale;
            var height = imageHeight * scale;
            return new System.Drawing.RectangleF(
                area.X + ((area.Width - width) / 2f),
                area.Y + ((area.Height - height) / 2f),
                width,
                height);
        }

        // A PaperSize built with the (name, width, height) constructor is PaperKind.Custom (RawKind 0
        // - measured), which reaches the driver as a user-defined size - and a driver with no
        // user-defined-size support just substitutes its own default paper, so an A4 sheet silently
        // prints letterboxed on Letter. The fabricated size could never match a real one by accident
        // either: rasterized pixel sizes round, so A4 can come back 826 against the real 827. Take
        // the driver's own entry when one is within 0.05in, which also absorbs that rounding.
        internal static System.Drawing.Printing.PaperSize ResolvePaperSize(
            System.Drawing.Printing.PrinterSettings settings, int widthHundredths, int heightHundredths)
        {
            const int toleranceHundredths = 5;
            try
            {
                var match = settings.PaperSizes.Cast<System.Drawing.Printing.PaperSize>()
                    .FirstOrDefault(p => p.Kind != System.Drawing.Printing.PaperKind.Custom
                                      && Math.Abs(p.Width - widthHundredths) <= toleranceHundredths
                                      && Math.Abs(p.Height - heightHundredths) <= toleranceHundredths);
                if (match != null) return match;
            }
            catch
            {
                // Enumerating the driver's paper list carries the same throw-on-virtual-printer risk
                // as CanDuplex above; a custom size still beats failing the print outright.
            }

            return new System.Drawing.Printing.PaperSize("PCR Sheet", widthHundredths, heightHundredths);
        }

        // PDF/XPS-writer "printers" are, in practice, the entire universe of drivers that turn a
        // print job into a save-a-file prompt - every real-world one (Microsoft Print to PDF, Adobe
        // PDF, Microsoft XPS Document Writer, CutePDF, PDFCreator, doPDF, Foxit PDF Printer, ...)
        // includes "PDF" or "XPS" in its display name.
        public bool IsFileWriterPrinter(string printerName)
            => printerName.Contains("PDF", StringComparison.OrdinalIgnoreCase)
               || printerName.Contains("XPS", StringComparison.OrdinalIgnoreCase);

        private static Microsoft.UI.Xaml.Window GetActiveWindow()
            => (Microsoft.UI.Xaml.Window)Microsoft.Maui.Controls.Application.Current!.Windows[0].Handler!.PlatformView!;

        private static async Task<string> SaveAndOpenFileAsync(byte[] bytes, string filename)
        {
            var targetDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                targetDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                targetDir = FileSystem.AppDataDirectory;
            }

            var filePath = Path.Combine(targetDir, filename);
            await File.WriteAllBytesAsync(filePath, bytes);

            // Attempt to open the generated file with the OS default application
            try
            {
                await Launcher.Default.OpenAsync(new OpenFileRequest
                {
                    Title = filename,
                    File = new ReadOnlyFile(filePath)
                });
            }
            catch
            {
                // Non-fatal if launcher cannot open in current environment
            }

            return filePath;
        }
    }
}
