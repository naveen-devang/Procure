using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;

namespace Procure.Services
{
    public class DashboardMetricsService : IDashboardMetricsService
    {
        private readonly IPurchaseRequisitionRepository _prRepo;
        private readonly ISettingsService _settings;

        public DashboardMetricsService(
            IPurchaseRequisitionRepository prRepo,
            ISettingsService settings)
        {
            _prRepo = prRepo;
            _settings = settings;
        }

        public async Task<DashboardMetrics> GetMetricsAsync()
        {
            var urgentThreshold = _settings.UrgentOverdueDays;
            var normalThreshold = _settings.NormalOverdueDays;

            // 1. Compute all aggregate counts/sums via direct, lightning-fast SQL queries (zero intermediate entity allocations)
            var aggregates = await _prRepo.GetDashboardAggregatesAsync(normalThreshold, urgentThreshold).ConfigureAwait(false);

            // 2. Load only the critical subset of PRs (< 10 items) needed for the Needs Attention widget
            var needsAttention = await _prRepo.GetNeedsAttentionPrsAsync(normalThreshold, urgentThreshold, 10).ConfigureAwait(false);

            var metrics = new DashboardMetrics
            {
                TotalPrs = aggregates.TotalPrs,
                TotalPoValue = aggregates.TotalPoValue,
                PosRaised = aggregates.PosRaised,
                RfqsAwaitingQuote = aggregates.RfqsAwaitingQuote,
                PcrsAwaitingSignature = aggregates.PcrsAwaitingSignature,
                UrgentCount = aggregates.UrgentCount,
                OverdueCount = aggregates.OverdueCount,
                NeedsAttentionPrs = new ObservableCollection<PurchaseRequisition>(needsAttention)
            };

            return metrics;
        }
    }
}
