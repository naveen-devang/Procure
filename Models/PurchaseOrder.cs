using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PurchaseOrder : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PrId { get; set; }

        [ObservableProperty]
        public partial string PoNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Vendor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial Guid? LinkedRfqId { get; set; }

        [ObservableProperty]
        public partial decimal Value { get; set; }

        [ObservableProperty]
        public partial string Status { get; set; } = PoStatus.Raised;

        [ObservableProperty]
        public partial DateTime? Date { get; set; } = DateTime.Today;

        [ObservableProperty]
        public partial string Currency { get; set; } = "AED";

        [ObservableProperty]
        public partial string CombinedPrs { get; set; } = string.Empty;

        [ObservableProperty]
        public partial decimal? BaseAmount { get; set; }

        [ObservableProperty]
        public partial decimal? Freight { get; set; }

        [ObservableProperty]
        public partial decimal? OtherCharges { get; set; }

        [ObservableProperty]
        public partial decimal? Discount { get; set; }

        [ObservableProperty]
        public partial string VatType { get; set; } = "5%";

        [ObservableProperty]
        public partial ObservableCollection<PurchaseOrderItem> Items { get; set; } = new();

        public bool IsCombinedPo => !string.IsNullOrWhiteSpace(CombinedPrs);
        public bool HasItems => Items != null && Items.Count > 0;
        public int ItemsCount => Items?.Count ?? 0;

        public string FormattedValue
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return $"{cur} {Value:N0}";
            }
        }
    }
}
