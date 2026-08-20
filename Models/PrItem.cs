using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PrItem : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PrId { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial string ItemName { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedQuantity))]
        [NotifyPropertyChangedFor(nameof(EstimatedTotalPrice))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        [NotifyPropertyChangedFor(nameof(PendingQuantity))]
        [NotifyPropertyChangedFor(nameof(IsFullyOrdered))]
        [NotifyPropertyChangedFor(nameof(IsPartiallyOrdered))]
        [NotifyPropertyChangedFor(nameof(IsUnordered))]
        [NotifyPropertyChangedFor(nameof(FulfillmentBadgeText))]
        public partial decimal Quantity { get; set; } = 1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedQuantity))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        [NotifyPropertyChangedFor(nameof(FulfillmentBadgeText))]
        public partial string Unit { get; set; } = "pcs";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EstimatedTotalPrice))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial decimal? EstimatedUnitPrice { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PendingQuantity))]
        [NotifyPropertyChangedFor(nameof(IsFullyOrdered))]
        [NotifyPropertyChangedFor(nameof(IsPartiallyOrdered))]
        [NotifyPropertyChangedFor(nameof(IsUnordered))]
        [NotifyPropertyChangedFor(nameof(FulfillmentBadgeText))]
        public partial decimal OrderedQuantity { get; set; }

        [ObservableProperty]
        public partial string Notes { get; set; } = string.Empty;

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        public decimal? EstimatedTotalPrice => EstimatedUnitPrice.HasValue ? Quantity * EstimatedUnitPrice.Value : null;

        public decimal PendingQuantity => Math.Max(0m, Quantity - OrderedQuantity);
        public bool IsFullyOrdered => Quantity > 0 && OrderedQuantity >= Quantity;
        public bool IsPartiallyOrdered => OrderedQuantity > 0 && OrderedQuantity < Quantity;
        public bool IsUnordered => OrderedQuantity == 0;

        public string FulfillmentBadgeText
        {
            get
            {
                var unitStr = string.IsNullOrWhiteSpace(Unit) ? "pcs" : Unit;
                if (IsFullyOrdered)
                {
                    return $"Ordered: {OrderedQuantity.ToString("G29", CultureInfo.InvariantCulture)}/{Quantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} (Complete)";
                }
                if (IsPartiallyOrdered)
                {
                    return $"Ordered: {OrderedQuantity.ToString("G29", CultureInfo.InvariantCulture)}/{Quantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} • {PendingQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} Pending";
                }
                return $"Unordered: 0/{Quantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} • {Quantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} Pending";
            }
        }

        public string FormattedQuantity
        {
            get
            {
                var unitStr = string.IsNullOrWhiteSpace(Unit) ? "pcs" : Unit;
                return $"{Quantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr}".Trim();
            }
        }

        public string FormattedDisplay
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(ItemName) ? "Unnamed Item" : ItemName;
                if (EstimatedUnitPrice.HasValue && EstimatedUnitPrice.Value > 0)
                {
                    return $"{name} — {FormattedQuantity} @ {EstimatedUnitPrice.Value:N2}";
                }
                return $"{name} — {FormattedQuantity}";
            }
        }

        public void NotifyThemeChanged()
        {
            OnPropertyChanged(nameof(FulfillmentBadgeText));
        }
    }
}
