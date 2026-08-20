using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;
using Procure.Models;

namespace Procure.Services
{
    public class CsvExportService : ICsvExportService
    {
        public Task<string> ExportPrsToCsvAsync(IEnumerable<PurchaseRequisition> prs, IEnumerable<CustomColumnDefinition> customColumns)
        {
            var sb = new StringBuilder();
            var colList = customColumns.OrderBy(c => c.SortOrder).ToList();

            // Header
            var headers = new List<string>
            {
                "PR Number",
                "Description",
                "Line Items Summary",
                "Items Count",
                "Requestor",
                "Plant",
                "Priority",
                "Status",
                "Created Date",
                "Age (Days)",
                "RFQ Count",
                "PCR Status",
                "PO Count",
                "Total PO Value",
                "Consolidated From",
                "Notes"
            };

            foreach (var col in colList)
            {
                headers.Add(EscapeCsv(col.Name));
            }

            sb.AppendLine(string.Join(",", headers));

            // Rows
            foreach (var pr in prs)
            {
                var row = new List<string>
                {
                    EscapeCsv(pr.PrNo),
                    EscapeCsv(pr.Description),
                    EscapeCsv(pr.ItemsSummary),
                    pr.ItemsCount.ToString(),
                    EscapeCsv(pr.Requestor),
                    EscapeCsv(pr.Plant),
                    EscapeCsv(pr.Priority),
                    EscapeCsv(pr.Status),
                    EscapeCsv(pr.CreatedAt.ToString("yyyy-MM-dd HH:mm")),
                    pr.AgeDays.ToString(),
                    pr.RfqCount.ToString(),
                    EscapeCsv(pr.PcrStatusDisplay),
                    pr.PoCount.ToString(),
                    pr.TotalPoValue.ToString("F2"),
                    EscapeCsv(pr.ConsolidatedFrom),
                    EscapeCsv(pr.Notes)
                };

                foreach (var col in colList)
                {
                    var customVal = pr.CustomValues.FirstOrDefault(v => v.ColumnId == col.Id)?.Value ?? string.Empty;
                    row.Add(EscapeCsv(customVal));
                }

                sb.AppendLine(string.Join(",", row));
            }

            return Task.FromResult(sb.ToString());
        }

        public async Task<string> SaveExportToFileAsync(string csvContent, string? filename = null)
        {
            filename ??= $"ProcurementExport_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            
            // Prefer user's Desktop or Documents or AppDataDirectory
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
            await File.WriteAllTextAsync(filePath, csvContent, Encoding.UTF8);
            return filePath;
        }

        private static string EscapeCsv(string? field)
        {
            if (string.IsNullOrEmpty(field)) return "\"\"";
            var text = field.Replace("\"", "\"\"");
            return $"\"{text}\"";
        }
    }
}
