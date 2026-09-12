using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Services
{
    public class CsvExportService : ICsvExportService
    {
        /// <summary>Writes every requisition to a CSV file, a row at a time.
        ///
        /// It used to build the whole file in a StringBuilder, call ToString() on it, and hand that
        /// string to File.WriteAllTextAsync to be encoded - so at 20,000 PRs the finished file existed
        /// three times over at the same moment, all of it on the large object heap. Now one row is in
        /// memory at a time, and the rows arrive from the repository in batches rather than as one
        /// list of everything.</summary>
        public async Task<string> WritePrsToFileAsync(
            IAsyncEnumerable<List<PurchaseRequisition>> batches,
            IEnumerable<CustomColumnDefinition> customColumns,
            string? filename = null)
        {
            var colList = customColumns.OrderBy(c => c.SortOrder).ToList();
            var filePath = Path.Combine(ExportDirectory(),
                filename ?? $"ProcurementExport_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            await using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
            await writer.WriteLineAsync(BuildHeader(colList)).ConfigureAwait(false);

            await foreach (var batch in batches.ConfigureAwait(false))
                foreach (var pr in batch)
                    await writer.WriteLineAsync(BuildRow(pr, colList)).ConfigureAwait(false);

            return filePath;
        }

        private static string BuildHeader(List<CustomColumnDefinition> colList)
        {
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
                "PO Currency",
                "PO Value Breakdown",
                "Consolidated From",
                "Notes"
            };

            foreach (var col in colList)
            {
                headers.Add(EscapeCsv(col.Name));
            }

            return string.Join(",", headers);
        }

        private static string BuildRow(PurchaseRequisition pr, List<CustomColumnDefinition> colList)
        {
                // "Total PO Value" was a bare cross-currency sum with no currency anywhere in the
                // file; the currency column flags it, and the breakdown itemizes mixed-currency PRs.
                var poCurrencies = pr.Pos?
                    .Select(p => string.IsNullOrWhiteSpace(p.Currency) ? "AED" : p.Currency)
                    .Distinct()
                    .ToList() ?? new List<string>();
                var poCurrencyDisplay = poCurrencies.Count == 0 ? "AED" : string.Join("/", poCurrencies);
                var poBreakdown = poCurrencies.Count > 1
                    ? string.Join(" | ", pr.Pos!
                        .GroupBy(p => string.IsNullOrWhiteSpace(p.Currency) ? "AED" : p.Currency)
                        .Select(g => $"{g.Key} {g.Sum(p => p.Value):F2}"))
                    : string.Empty;

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
                    EscapeCsv(poCurrencyDisplay),
                    EscapeCsv(poBreakdown),
                    EscapeCsv(pr.ConsolidatedFrom),
                    EscapeCsv(pr.Notes)
                };

                foreach (var col in colList)
                {
                    var customVal = pr.CustomValues.FirstOrDefault(v => v.ColumnId == col.Id)?.Value ?? string.Empty;
                    row.Add(EscapeCsv(customVal));
                }

            return string.Join(",", row);
        }

        /// <summary>Where an export lands: the Desktop if there is one, then Documents, then the
        /// app's own folder.</summary>
        private static string ExportDirectory()
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
            return targetDir;
        }

        private static string EscapeCsv(string? field)
        {
            if (string.IsNullOrEmpty(field)) return "\"\"";
            var text = field.Replace("\"", "\"\"");
            return $"\"{text}\"";
        }
    }
}
