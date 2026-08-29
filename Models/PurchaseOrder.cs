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
        [NotifyPropertyChangedFor(nameof(FormattedValue))]
        public partial decimal Value { get; set; }

        [ObservableProperty]
        public partial string Status { get; set; } = PoStatus.Raised;

        [ObservableProperty]
        public partial DateTime? Date { get; set; } = DateTime.Today;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedValue))]
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

        // Transport details, Raw Material POs only. Deliberately never folded into Value/BaseAmount -
        // haulage is tracked here so it shows next to the PO, not inside its total.
        [ObservableProperty]
        public partial string? TransportContractNumber { get; set; }

        [ObservableProperty]
        public partial string? TransporterName { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedTransportRatePerUnit))]
        public partial decimal? TransportRatePerUnit { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedTransportTotal))]
        public partial decimal? TransportTotal { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<PurchaseOrderItem> Items { get; set; } = new();

        public bool IsCombinedPo => !string.IsNullOrWhiteSpace(CombinedPrs);
        public bool HasItems => Items != null && Items.Count > 0;
        public int ItemsCount => Items?.Count ?? 0;

        public bool HasTransportDetails => TransportRatePerUnit.HasValue || TransportTotal.HasValue
            || !string.IsNullOrWhiteSpace(TransportContractNumber) || !string.IsNullOrWhiteSpace(TransporterName);

        public string FormattedValue => Procure.Utilities.MoneyFormat.Format(Currency, Value);
        public string FormattedTransportRatePerUnit => TransportRatePerUnit.HasValue
            ? Procure.Utilities.MoneyFormat.Format(Currency, TransportRatePerUnit.Value) : string.Empty;
        public string FormattedTransportTotal => TransportTotal.HasValue
            ? Procure.Utilities.MoneyFormat.Format(Currency, TransportTotal.Value) : string.Empty;
    }
}
