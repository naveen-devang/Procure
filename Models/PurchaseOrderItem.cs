using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PurchaseOrderItem : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PoId { get; set; }
        public Guid? PrItemId { get; set; }
        public Guid? RfqItemId { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial string ItemName { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedQuantity))]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial decimal Quantity { get; set; } = 1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedQuantity))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial string Unit { get; set; } = "pcs";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedUnitPrice))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial decimal? UnitPrice { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedDiscount))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        public partial decimal? Discount { get; set; }

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        public decimal LineTotal => (UnitPrice.HasValue && UnitPrice.Value > 0)
            ? Quantity * Math.Max(0m, UnitPrice.Value - (Discount ?? 0m))
            : 0m;

        public string FormattedQuantity
        {
            get
            {
                var unitStr = string.IsNullOrWhiteSpace(Unit) ? "pcs" : Unit;
                return $"{Quantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr}".Trim();
            }
        }

        public string FormattedUnitPrice
        {
            get
            {
                if (UnitPrice.HasValue && UnitPrice.Value > 0)
                {
                    return UnitPrice.Value.ToString("N2", CultureInfo.InvariantCulture);
                }
                return string.Empty;
            }
        }

        public string FormattedDiscount
        {
            get
            {
                if (Discount.HasValue && Discount.Value > 0)
                {
                    return Discount.Value.ToString("N2", CultureInfo.InvariantCulture);
                }
                return string.Empty;
            }
        }

        public string FormattedLineTotal
        {
            get
            {
                if (UnitPrice.HasValue && UnitPrice.Value > 0)
                {
                    return LineTotal.ToString("N2", CultureInfo.InvariantCulture);
                }
                return "-";
            }
        }

        public string FormattedDisplay
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(ItemName) ? "Item" : ItemName;
                if (UnitPrice.HasValue && UnitPrice.Value > 0)
                {
                    return $"{name} ({FormattedQuantity} @ {UnitPrice.Value:N2} = {LineTotal:N2})";
                }
                return $"{name} ({FormattedQuantity})";
            }
        }
    }
}
