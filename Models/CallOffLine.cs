using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    // One row per PO item eligible for call-off tracking (Raw/Packing Material PRs only).
    // CalledOffQuantity is the pre-aggregated sum from PoItemCallOff, kept in sync locally
    // after each log/delete so the group totals never need a full reload to stay accurate.
    public partial class CallOffLine : ObservableObject
    {
        public Guid PoItemId { get; set; }
        public string MaterialName { get; set; } = string.Empty;
        public string Vendor { get; set; } = string.Empty;
        public string PoNo { get; set; } = string.Empty;
        public string PrNo { get; set; } = string.Empty;
        public decimal OrderedQuantity { get; set; }
        public string Unit { get; set; } = "pcs";

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RemainingQuantity))]
        [NotifyPropertyChangedFor(nameof(PercentComplete))]
        [NotifyPropertyChangedFor(nameof(Percent01))]
        [NotifyPropertyChangedFor(nameof(FormattedCalledOff))]
        [NotifyPropertyChangedFor(nameof(FormattedRemaining))]
        public partial decimal CalledOffQuantity { get; set; }

        public decimal RemainingQuantity => Math.Max(0, OrderedQuantity - CalledOffQuantity);

        public double PercentComplete => OrderedQuantity <= 0 ? 0 : (double)Math.Min(1m, CalledOffQuantity / OrderedQuantity) * 100.0;

        public double Percent01 => PercentComplete / 100.0;

        public string FormattedOrdered => $"{OrderedQuantity.ToString("G29", CultureInfo.InvariantCulture)} {Unit}";
        public string FormattedCalledOff => $"{CalledOffQuantity.ToString("G29", CultureInfo.InvariantCulture)} {Unit}";
        public string FormattedRemaining => $"{RemainingQuantity.ToString("G29", CultureInfo.InvariantCulture)} {Unit}";
    }
}
