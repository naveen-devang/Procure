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

        Task<string> ExportPcrToPdfAsync(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks);
    }
}
