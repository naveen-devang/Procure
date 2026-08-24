using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Data.Repositories
{
    /// <summary>Everything the board filters by, in the shape the SQL needs it. The overdue thresholds
    /// travel with the query because they are user settings, not constants.</summary>
    public sealed record PrQuery(
        string? Search,
        string? Status,
        bool OverdueOnly,
        bool PcrPendingOnly,
        bool UrgentOnly,
        int NormalOverdueDays,
        int UrgentOverdueDays,
        int Skip,
        int Take);

    /// <summary><paramref name="TotalCount"/> is the unpaged match count, for the board's footer and
    /// for knowing when the infinite scroll has reached the end.</summary>
    public sealed record PrPage(List<PurchaseRequisition> Rows, int TotalCount);

    public interface IPurchaseRequisitionRepository
    {
        /// <summary>One page of the board with its full child graph. This is the board's read path;
        /// GetAllAsync remains only for the CSV export, which genuinely wants every row.</summary>
        Task<PrPage> GetPageAsync(PrQuery query);

        /// <summary>Specific PRs with their child graph - how the board keeps a selection loaded once it
        /// scrolls outside the visible window.</summary>
        Task<List<PurchaseRequisition>> GetByIdsAsync(IReadOnlyCollection<Guid> ids);

        /// <summary>The source PRs behind a merged master. Merged children are hidden from the board's
        /// default view, so this cannot be answered from the loaded window.</summary>
        Task<List<PurchaseRequisition>> GetChildPrsAsync(Guid masterPrId, IReadOnlyCollection<string> fallbackPrNos);

        Task<List<PurchaseRequisition>> GetAllAsync();
        Task<int> GetCountAsync();
        Task SaveAsync(PurchaseRequisition pr);
        /// <summary>UPSERTs the PurchaseRequisition row only - no PrItem, no CustomFieldValue writes.</summary>
        Task SavePrFieldsAsync(PurchaseRequisition pr);
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
