using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Procure.Utilities;

namespace Procure.Models
{
    public partial class PrItem : ObservableModel
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
        [NotifyPropertyChangedFor(nameof(IsOverOrdered))]
        [NotifyPropertyChangedFor(nameof(OverOrderedQuantity))]
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

        /// <summary>What the last purchase of this item was actually priced in - not always the PR's
        /// own currency. Null means "not yet typed"; <see cref="EstimatedPriceText"/> resolves it to
        /// "AED" the same way <see cref="Utilities.MoneyFormat"/> does everywhere else.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedDisplay))]
        public partial string? EstimatedCurrency { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PendingQuantity))]
        [NotifyPropertyChangedFor(nameof(IsFullyOrdered))]
        [NotifyPropertyChangedFor(nameof(IsPartiallyOrdered))]
        [NotifyPropertyChangedFor(nameof(IsUnordered))]
        [NotifyPropertyChangedFor(nameof(IsOverOrdered))]
        [NotifyPropertyChangedFor(nameof(OverOrderedQuantity))]
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

        /// <summary>More has been ordered than the line now asks for - almost always because the
        /// requisition was cut back after the PO went out. Pending floors at zero, so without this
        /// the line reported a cheerful "Ordered 40/25 (Complete)" and the surplus was invisible.</summary>
        public bool IsOverOrdered => Quantity > 0 && OrderedQuantity > Quantity;
        public decimal OverOrderedQuantity => Math.Max(0m, OrderedQuantity - Quantity);

        public string FulfillmentBadgeText
        {
            get
            {
                var unitStr = string.IsNullOrWhiteSpace(Unit) ? "pcs" : Unit;
                if (IsOverOrdered)
                {
                    return $"Over-ordered by {OverOrderedQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} (Ordered: {OrderedQuantity.ToString("G29", CultureInfo.InvariantCulture)}, PR asks for {Quantity.ToString("G29", CultureInfo.InvariantCulture)})";
                }
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

        /// <summary>What the estimated-price box reads and writes. Typing a currency alongside the
        /// number - "86.75 usd", "$86.75" - sets <see cref="EstimatedCurrency"/> too; a bare number
        /// leaves whatever currency was already there. The setter does not echo its own change back
        /// (mirrors <see cref="RfqItem.LastPriceText"/>) so mid-type the caret never jumps.
        ///
        /// Always shows the currency, not just the number - reopening this row after months away
        /// with a bare "86.75" left no way to tell what it was actually priced in.</summary>
        public string EstimatedPriceText
        {
            get => EstimatedUnitPrice.HasValue
                ? $"{EstimatedUnitPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)} {(string.IsNullOrWhiteSpace(EstimatedCurrency) ? "AED" : EstimatedCurrency.ToUpperInvariant())}"
                : string.Empty;
            set
            {
                _suppressEstimatedPriceTextEcho = true;
                try
                {
                    var (amount, currency) = SmartPriceParser.Parse(value, EstimatedCurrency ?? "AED");
                    EstimatedUnitPrice = amount;
                    EstimatedCurrency = currency;
                }
                finally
                {
                    _suppressEstimatedPriceTextEcho = false;
                }
            }
        }

        private bool _suppressEstimatedPriceTextEcho;

        partial void OnEstimatedUnitPriceChanged(decimal? value) { RaiseEstimatedPriceTextChanged(); ForgetPriceSource(); }
        partial void OnEstimatedCurrencyChanged(string? value) { RaiseEstimatedPriceTextChanged(); ForgetPriceSource(); }

        // ---- filled from a past PO (see PrListPageModel.FillPrItemPricesAsync) --------------------

        /// <summary>Set when the price was filled in from a past PO: "PO-2914-1 · Dell · 12 Aug 2026".
        /// Shown under the box for as long as the form is open, never saved. Any change to the price
        /// or its currency clears it - the figure is then the user's, not that PO's.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasEstimatedPriceSource))]
        [NotifyPropertyChangedFor(nameof(EstimatedPriceSourceShort))]
        public partial string EstimatedPriceSource { get; set; } = string.Empty;

        public bool HasEstimatedPriceSource => EstimatedPriceSource.Length > 0;

        /// <summary>The part that fits under a 95 px box: "PO-2914-1".</summary>
        public string EstimatedPriceSourceShort
        {
            get
            {
                var cut = EstimatedPriceSource.IndexOf("  ·  ", StringComparison.Ordinal);
                return cut < 0 ? EstimatedPriceSource : EstimatedPriceSource[..cut];
            }
        }

        /// <summary>The item name the filled price was looked up for, so a rename can tell that the
        /// price belongs to the old name.</summary>
        public string? PriceFilledForName { get; private set; }

        private bool _fillingPrice;

        private void ForgetPriceSource()
        {
            if (_fillingPrice) return;
            EstimatedPriceSource = string.Empty;
            PriceFilledForName = null;
        }

        public void FillEstimatedPrice(LastPaidPrice paid, string forName)
        {
            _fillingPrice = true;
            try
            {
                EstimatedUnitPrice = paid.Price;
                EstimatedCurrency = paid.Currency;
            }
            finally { _fillingPrice = false; }
            EstimatedPriceSource = paid.Source;
            PriceFilledForName = forName;
        }

        /// <summary>A filled price whose item was renamed to something never bought: it described the
        /// old item, so it goes.</summary>
        public void ClearFilledEstimate()
        {
            _fillingPrice = true;
            try
            {
                EstimatedUnitPrice = null;
                EstimatedCurrency = null;
            }
            finally { _fillingPrice = false; }
            EstimatedPriceSource = string.Empty;
            PriceFilledForName = null;
        }

        private void RaiseEstimatedPriceTextChanged()
        {
            if (_suppressEstimatedPriceTextEcho) return;
            OnPropertyChanged(nameof(EstimatedPriceText));
        }

        public string FormattedDisplay
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(ItemName) ? "Unnamed Item" : ItemName;
                if (EstimatedUnitPrice.HasValue && EstimatedUnitPrice.Value > 0)
                {
                    return $"{name} — {FormattedQuantity} @ {MoneyFormat.Format(EstimatedCurrency, EstimatedUnitPrice.Value)}";
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
