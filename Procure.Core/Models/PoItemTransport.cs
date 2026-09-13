using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    /// <summary>
    /// One contract a PO line's quantity moves under. A line normally has exactly one; it has two
    /// when the quantity is split across contracts - 60 tonnes on one, 40 on another - which used
    /// to be done by cutting the PO line in half, taking the delivery tracking with it.
    ///
    /// Only orders in TransportMode 'Line' have these. A whole-order PO keeps its single contract
    /// on PurchaseOrder, where it has always been.
    /// </summary>
    public partial class PoItemTransport : ObservableModel
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PoItemId { get; set; }

        /// <summary>How much of the line's quantity travels under this contract.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Total))]
        [NotifyPropertyChangedFor(nameof(FormattedTotal))]
        public partial decimal Quantity { get; set; }

        [ObservableProperty]
        public partial string? ContractNumber { get; set; }

        [ObservableProperty]
        public partial string? TransporterName { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Total))]
        [NotifyPropertyChangedFor(nameof(FormattedTotal))]
        public partial decimal? RatePerUnit { get; set; }

        public int SortOrder { get; set; }

        /// <summary>Rate is per unit, always - confirmed with the user rather than assumed.</summary>
        public decimal Total => Quantity * (RatePerUnit ?? 0m);

        /// <summary>Set by whoever builds the row so money formats in the order's currency.</summary>
        public string Currency { get; set; } = "AED";

        public string FormattedTotal => Procure.Utilities.MoneyFormat.Format(Currency, Total);

        public string ContractLabel =>
            string.IsNullOrWhiteSpace(ContractNumber)
                ? (TransporterName ?? string.Empty)
                : string.IsNullOrWhiteSpace(TransporterName)
                    ? ContractNumber!
                    : $"{ContractNumber} · {TransporterName}";
    }
}
