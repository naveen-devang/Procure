using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Services.Export
{
    public interface IPcrExportService
    {
        Task<string> ExportPcrToExcelAsync(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks);

        byte[] GeneratePcrPdfBytes(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks,
            PcrPdfOptions options);

        // Null return means the user cancelled the save dialog.
        Task<string?> SavePcrPdfAsync(byte[] pdfBytes, string suggestedFileName);

        IReadOnlyList<string> GetAvailablePrinters();

        string GetDefaultPrinterName();

        // Prints the same rasterized bitmaps the preview modal shows, so the printed sheet always
        // matches what was on screen rather than whatever scaling a printer driver's own dialog picks.
        // pageIndices is 0-based; null or empty means every page. A PDF/XPS-writer "printer" is routed
        // through the Save As file picker instead of GDI printing, since that's what it actually does
        // and it's the only way to get a real answer on whether the user went through with it; the
        // returned bool reflects that (false = the save/print was cancelled).
        Task<bool> PrintPcrPdfAsync(byte[] pdfBytes, string printerName, string jobTitle, bool doubleSided, IReadOnlyList<int>? pageIndices, int copies = 1);

        // True for a "printer" that actually saves a file (Microsoft Print to PDF, Adobe PDF, XPS
        // Writer, ...). The caller uses it to hand PrintPcrPdfAsync a PDF already cut down to the
        // chosen page range, since a saved file has no printer to apply the range for it.
        bool IsFileWriterPrinter(string printerName);
    }
}
