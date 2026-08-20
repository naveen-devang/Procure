using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Services
{
    public interface ICsvExportService
    {
        Task<string> ExportPrsToCsvAsync(IEnumerable<PurchaseRequisition> prs, IEnumerable<CustomColumnDefinition> customColumns);
        Task<string> SaveExportToFileAsync(string csvContent, string? filename = null);
    }
}
