using System;
using System.Collections.Generic;
using System.IO;
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
            var filePath = await SaveAndOpenFileAsync(bytes, filename);
            return filePath;
        }

        public async Task<string> ExportPcrToPdfAsync(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks)
        {
            var bytes = PcrPdfExporter.GeneratePdf(pr, pcr, selectedRfqs, remarks);
            var safePrNo = string.IsNullOrWhiteSpace(pr.PrNo) ? "PR" : pr.PrNo.Replace("/", "-").Replace("\\", "-");
            var filename = $"PriceComparison_{safePrNo}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            var filePath = await SaveAndOpenFileAsync(bytes, filename);
            return filePath;
        }

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
