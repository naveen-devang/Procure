using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class RfqItem : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid RfqId { get; set; }
        public Guid? PrItemId { get; set; }

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
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial bool IsQuoted { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedQuotedUnitPrice))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial decimal? QuotedUnitPrice { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedDiscount))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial decimal? Discount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedLastPrice))]
        public partial decimal? LastPrice { get; set; }

        [ObservableProperty]
        public partial string Notes { get; set; } = string.Empty;

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        public decimal LineTotal => (IsQuoted && QuotedUnitPrice.HasValue && QuotedUnitPrice.Value > 0) 
            ? Quantity * Math.Max(0m, QuotedUnitPrice.Value - (Discount ?? 0m)) 
            : 0m;

        public string FormattedQuantity
        {
            get
            {
                var unitStr = string.IsNullOrWhiteSpace(Unit) ? "pcs" : Unit;
                return $"{Quantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr}".Trim();
            }
        }

        public string FormattedQuotedUnitPrice
        {
            get
            {
                if (QuotedUnitPrice.HasValue && QuotedUnitPrice.Value > 0)
                {
                    return QuotedUnitPrice.Value.ToString("N2", CultureInfo.InvariantCulture);
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

        public string FormattedLastPrice
        {
            get
            {
                if (LastPrice.HasValue && LastPrice.Value > 0)
                {
                    return LastPrice.Value.ToString("N2", CultureInfo.InvariantCulture);
                }
                return string.Empty;
            }
        }

        public string FormattedLineTotal
        {
            get
            {
                if (IsQuoted && QuotedUnitPrice.HasValue && QuotedUnitPrice.Value > 0)
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
                if (IsQuoted && QuotedUnitPrice.HasValue && QuotedUnitPrice.Value > 0)
                {
                    return $"{name} ({FormattedQuantity} @ {QuotedUnitPrice.Value:N2} = {LineTotal:N2})";
                }
                return $"{name} ({FormattedQuantity})";
            }
        }
    }
}
