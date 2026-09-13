using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Services
{
    public interface ICsvExportService
    {
        /// <summary>Writes the export straight to a file, a row at a time, and returns its path.</summary>
        Task<string> WritePrsToFileAsync(
            IAsyncEnumerable<List<PurchaseRequisition>> batches,
            IEnumerable<CustomColumnDefinition> customColumns,
            string? filename = null);
    }
}
