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

        public async Task<bool> PrintPcrPdfAsync(byte[] pdfBytes, string printerName, string jobTitle, bool doubleSided, IReadOnlyList<int>? pageIndices)
        {
            // A "printer" that's actually a PDF/XPS writer (Microsoft Print to PDF, Adobe PDF, ...)
            // shows its own Save-As dialog after the job is spooled - and GDI's PrintDocument.Print()
            // returns successfully either way, whether the user went through with that dialog or
            // cancelled it. There is no reliable signal back from that dialog through the print
            // pipeline: spooler job status was tried and proved too racy (a cancelled job can leave
            // the queue before it's ever observed as failed, indistinguishable from a fast success).
            // Since what these "printers" actually do IS save a file, route them through the exact
            // file-save flow used by Save As instead, which already reports cancellation correctly.
            // Page range/duplex don't carry over to this path - neither applies to a saved file.
            if (IsFileWriterPrinter(printerName))
            {
                var safeName = string.Concat(jobTitle.Split(Path.GetInvalidFileNameChars()));
                var savedPath = await SavePcrPdfAsync(pdfBytes, $"{safeName}.pdf");
                return savedPath != null;
            }

            var allPages = await PcrPdfRasterizer.RenderPagesAsync(pdfBytes);
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
                using (var firstImage = System.Drawing.Image.FromStream(new MemoryStream(allPages[selectedPages[0]])))
                {
                    bool isLandscape = firstImage.Width > firstImage.Height;
                    double widthIn = firstImage.Width / PcrPdfRasterizer.DefaultDpi;
                    double heightIn = firstImage.Height / PcrPdfRasterizer.DefaultDpi;

                    // PaperSize.Width/Height are always the sheet's un-rotated (portrait-convention)
                    // dimensions - Landscape is what actually flips how it's fed/measured. Storing
                    // the already-landscape pixel size directly here would make GDI swap it a
                    // second time when Landscape is applied.
                    var (paperWidthIn, paperHeightIn) = isLandscape ? (heightIn, widthIn) : (widthIn, heightIn);

                    printDocument.DefaultPageSettings.Landscape = isLandscape;
                    printDocument.DefaultPageSettings.PaperSize = new System.Drawing.Printing.PaperSize(
                        "PCR Sheet",
                        (int)Math.Round(paperWidthIn * 100),
                        (int)Math.Round(paperHeightIn * 100));
                }

                // CanDuplex queries the driver and has been seen to throw on some virtual/PDF
                // printers rather than just returning false - guarded so a duplex request never
                // blocks the actual print, it just silently prints one-sided instead.
                var canDuplex = false;
                try { canDuplex = printDocument.PrinterSettings.CanDuplex; } catch { /* treat as no duplex support */ }

                printDocument.PrinterSettings.Duplex = doubleSided && canDuplex
                    ? System.Drawing.Printing.Duplex.Vertical
                    : System.Drawing.Printing.Duplex.Simplex;

                var cursor = 0;
                printDocument.PrintPage += (_, e) =>
                {
                    using var image = System.Drawing.Image.FromStream(new MemoryStream(allPages[selectedPages[cursor]]));

                    // Fit the rasterized page into the printable area ourselves, preserving aspect
                    // ratio, so the printed sheet matches the on-screen preview instead of whatever
                    // scaling a printer driver's own dialog would otherwise apply.
                    var bounds = e.MarginBounds;
                    var scale = Math.Min((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
                    var width = (int)(image.Width * scale);
                    var height = (int)(image.Height * scale);
                    var x = bounds.X + ((bounds.Width - width) / 2);
                    var y = bounds.Y + ((bounds.Height - height) / 2);
                    e.Graphics!.DrawImage(image, x, y, width, height);

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

        // PDF/XPS-writer "printers" are, in practice, the entire universe of drivers that turn a
        // print job into a save-a-file prompt - every real-world one (Microsoft Print to PDF, Adobe
        // PDF, Microsoft XPS Document Writer, CutePDF, PDFCreator, doPDF, Foxit PDF Printer, ...)
        // includes "PDF" or "XPS" in its display name.
        private static bool IsFileWriterPrinter(string printerName)
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
