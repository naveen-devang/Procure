using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public interface IPurchaseRequisitionRepository
    {
        Task<List<PurchaseRequisition>> GetAllAsync();
        Task<int> GetCountAsync();
        Task SaveAsync(PurchaseRequisition pr);
        Task DeleteAsync(Guid id);

        Task SaveRfqAsync(RequestForQuotation rfq);
        Task DeleteRfqAsync(Guid rfqId);

        Task SavePcrAsync(PriceComparisonRequest pcr);
        Task UpdateApprovalAsync(Approval approval);

        Task SavePoAsync(PurchaseOrder po);
        Task DeletePoAsync(Guid poId);

        Task MergePrsAsync(List<PurchaseRequisition> sourcePrs, PurchaseRequisition masterPr, bool copyRfqs);
        Task SaveBatchPrsAsync(List<PurchaseRequisition> prs);
        Task CreateBatchPoAsync(List<PurchaseRequisition> targetPrs, PurchaseOrder poTemplate);
        Task CreateBatchRfqAsync(List<PurchaseRequisition> targetPrs, RequestForQuotation rfqTemplate, IEnumerable<RfqItem>? batchItems = null);
        Task SplitMergedPrAsync(Guid masterPrId);
        Task PartialSplitMergedPrAsync(Guid masterPrId, List<PurchaseRequisition> splitPrs, List<PurchaseRequisition> keptPrs);
        Task SplitSharedRfqAsync(Guid rfqId);
        Task SplitCombinedPoAsync(Guid poId);

        Task<(int TotalPrs, decimal TotalPoValue, int PosRaised, int RfqsAwaitingQuote, int PcrsAwaitingSignature, int UrgentCount, int OverdueCount)> GetDashboardAggregatesAsync(int normalOverdueDays, int urgentOverdueDays);
        Task<List<PurchaseRequisition>> GetNeedsAttentionPrsAsync(int normalOverdueDays, int urgentOverdueDays, int limit = 10);
    }
}
