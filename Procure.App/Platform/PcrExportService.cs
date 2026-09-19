using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Procure.Models;
using Procure.Services.Export;

namespace Procure.App.Platform;

// WinUI port of the MAUI PcrExportService. The picker/print/rasterize code was already
// pure WinRT + System.Drawing; only the window-handle grab (was via MAUI's Application)
// and "open the file afterwards" (was Launcher.Default) needed swapping.
public sealed class PcrExportService : IPcrExportService
{
    private readonly ShellContext _shell;
    public PcrExportService(ShellContext shell) => _shell = shell;

    public async Task<string> ExportPcrToExcelAsync(
        PurchaseRequisition pr,
        PriceComparisonRequest pcr,
        IReadOnlyList<RequestForQuotation> selectedRfqs,
        string remarks,
        IReadOnlyList<PrItem>? selectedItems = null)
    {
        var bytes = PcrExcelExporter.GenerateExcel(pr, pcr, selectedRfqs, remarks, selectedItems);
        var safePrNo = string.IsNullOrWhiteSpace(pr.PrNo) ? "PR" : pr.PrNo.Replace("/", "-").Replace("\\", "-");
        var filename = $"PriceComparison_{safePrNo}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return await SaveAndOpenFileAsync(bytes, filename);
    }

    public byte[] GeneratePcrPdfBytes(
        PurchaseRequisition pr,
        PriceComparisonRequest pcr,
        IReadOnlyList<RequestForQuotation> selectedRfqs,
        string remarks,
        PcrPdfOptions options,
        IReadOnlyList<PrItem>? selectedItems = null)
        => PcrPdfExporter.GeneratePdf(pr, pcr, selectedRfqs, remarks, options, selectedItems);

    public async Task<string?> SavePcrPdfAsync(byte[] pdfBytes, string suggestedFileName)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_shell.Window);

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
        OpenWithDefaultApp(file.Path);
        return file.Path;
    }

    // Printer names only, via classic Windows print-spooler enumeration.
    public IReadOnlyList<string> GetAvailablePrinters()
        => System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().ToList();

    public string GetDefaultPrinterName()
        => new System.Drawing.Printing.PrinterSettings().PrinterName;

    public async Task<bool> PrintPcrPdfAsync(byte[] pdfBytes, string printerName, string jobTitle, bool doubleSided, IReadOnlyList<int>? pageIndices, int copies = 1)
    {
        // A PDF/XPS-writer "printer" actually saves a file - route it through the file-save
        // flow, which reports cancellation correctly (GDI's Print() does not).
        if (IsFileWriterPrinter(printerName))
        {
            var safeName = string.Concat(jobTitle.Split(Path.GetInvalidFileNameChars()));
            var savedPath = await SavePcrPdfAsync(pdfBytes, $"{safeName}.pdf");
            return savedPath != null;
        }

        // Black and white at 600 DPI, or the printer's own resolution when lower - see
        // PcrPdfRasterizer.PrintDpiFor for the measurements behind it.
        double printerDpi = 0;
        try
        {
            var probe = new System.Drawing.Printing.PrinterSettings();
            if (!string.IsNullOrWhiteSpace(printerName)) probe.PrinterName = printerName;
            printerDpi = probe.DefaultPageSettings.PrinterResolution.X;   // negative = a quality preset, not DPI
        }
        catch { /* unknown printer resolution: no cap */ }
        var requestedDpi = PcrPdfRasterizer.PrintDpiFor(printerDpi);

        // Opened once; each page is drawn only when the printer asks for it and released after, so a
        // print holds one page image however many pages the sheet has.
        var source = await PcrPdfPageSource.OpenAsync(pdfBytes, requestedDpi);
        var renderDpi = source.EffectiveDpi;
        if (source.PageCount == 0) { source.Dispose(); return true; }
        if (Environment.GetEnvironmentVariable("PROCURE_PRINT_LOG") == "1")
            Procure.Utilities.CrashLog.Write($"PCR print: {renderDpi:0} DPI black and white (printer reports {printerDpi:0}), {source.PageCount} page(s)");

        var selectedPages = (pageIndices == null || pageIndices.Count == 0)
            ? Enumerable.Range(0, source.PageCount).ToList()
            : pageIndices.Where(i => i >= 0 && i < source.PageCount).Distinct().OrderBy(i => i).ToList();
        if (selectedPages.Count == 0) selectedPages = Enumerable.Range(0, source.PageCount).ToList();

        return await Task.Run(() =>
        {
            using var sourceLifetime = source;
            using var printDocument = new System.Drawing.Printing.PrintDocument();
            if (!string.IsNullOrWhiteSpace(printerName))
            {
                printDocument.PrinterSettings.PrinterName = printerName;
            }
            printDocument.DocumentName = jobTitle;

            // The rasterized bitmap carries this job's real geometry (PcrPdfExporter's chosen
            // orientation/paper size); read it back from the first page's pixel dimensions.
            bool isLandscapeJob;
            {
                var (widthIn, heightIn) = source.PageSizeInches(selectedPages[0]);
                bool isLandscape = isLandscapeJob = widthIn > heightIn;

                var (paperWidthIn, paperHeightIn) = isLandscape ? (heightIn, widthIn) : (widthIn, heightIn);

                printDocument.DefaultPageSettings.Landscape = isLandscape;
                printDocument.DefaultPageSettings.PaperSize = ResolvePaperSize(
                    printDocument.PrinterSettings,
                    (int)Math.Round(paperWidthIn * 100),
                    (int)Math.Round(paperHeightIn * 100));
            }

            int maxCopies = short.MaxValue;
            try { maxCopies = printDocument.PrinterSettings.MaximumCopies; } catch { /* keep the request */ }
            printDocument.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, Math.Max(1, maxCopies));
            printDocument.PrinterSettings.Collate = true;

            var canDuplex = false;
            try { canDuplex = printDocument.PrinterSettings.CanDuplex; } catch { /* treat as no duplex support */ }

            printDocument.PrinterSettings.Duplex = doubleSided && canDuplex
                ? (isLandscapeJob ? System.Drawing.Printing.Duplex.Horizontal : System.Drawing.Printing.Duplex.Vertical)
                : System.Drawing.Printing.Duplex.Simplex;

            var cursor = 0;
            printDocument.PrintPage += (_, e) =>
            {
                // PdfDocument is agile, so rendering here on the print thread is fine.
                var page = source.RenderMonochromeAsync(selectedPages[cursor]).GetAwaiter().GetResult();
                using var image = ToBitmap(page);

                var dest = PlacePage(e.Graphics!, image.Width, image.Height);

                // Nearest neighbour: smoothing a black-and-white page on its way to the printer would
                // turn it back into greys and undo the point of sending it that way.
                e.Graphics!.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                e.Graphics.DrawImage(image, dest);

                cursor++;
                e.HasMorePages = cursor < selectedPages.Count;
            };

            printDocument.Print();
            return true;
        });
    }

    /// <summary>A one-bit GDI+ bitmap over a rendered black-and-white page. One bit per pixel is what
    /// keeps the print job small: an A4 page at 600 DPI records as 4.3 MB, against 33.7 MB in colour.</summary>
    internal static System.Drawing.Bitmap ToBitmap(MonochromePage page)
    {
        var bitmap = new System.Drawing.Bitmap(page.Width, page.Height, System.Drawing.Imaging.PixelFormat.Format1bppIndexed);
        bitmap.SetResolution((float)page.Dpi, (float)page.Dpi);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, page.Width, page.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format1bppIndexed);
        try
        {
            int rowBytes = Math.Min(Math.Abs(data.Stride), page.Stride);
            for (int y = 0; y < page.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(page.Bits, y * page.Stride, data.Scan0 + y * data.Stride, rowBytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    internal static System.Drawing.RectangleF PlacePage(System.Drawing.Graphics g, int imageWidth, int imageHeight)
        => FitInto(imageWidth, imageHeight, g.VisibleClipBounds);

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
            // Enumerating the driver's paper list can throw on virtual printers.
        }

        return new System.Drawing.Printing.PaperSize("PCR Sheet", widthHundredths, heightHundredths);
    }

    public bool IsFileWriterPrinter(string printerName)
        => printerName.Contains("PDF", StringComparison.OrdinalIgnoreCase)
           || printerName.Contains("XPS", StringComparison.OrdinalIgnoreCase);

    private async Task<string> SaveAndOpenFileAsync(byte[] bytes, string filename)
    {
        var targetDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            targetDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            targetDir = Procure.AppPaths.AppData;
        }

        var filePath = Path.Combine(targetDir, filename);
        await File.WriteAllBytesAsync(filePath, bytes);
        OpenWithDefaultApp(filePath);
        return filePath;
    }

    private static void OpenWithDefaultApp(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal if no viewer is available.
        }
    }
}
