using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class DashboardMetrics : ObservableObject
    {
        [ObservableProperty]
        public partial int TotalPrs { get; set; }

        [ObservableProperty]
        public partial int RfqsAwaitingQuote { get; set; }

        [ObservableProperty]
        public partial int PcrsAwaitingSignature { get; set; }

        [ObservableProperty]
        public partial int PosRaised { get; set; }

        [ObservableProperty]
        public partial decimal TotalPoValue { get; set; }

        [ObservableProperty]
        public partial int OverdueCount { get; set; }

        [ObservableProperty]
        public partial int UrgentCount { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<PurchaseRequisition> NeedsAttentionPrs { get; set; } = new();
    }
}
