using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class RfqItem : ObservableModel, Procure.Utilities.IPrLine
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

        /// <summary>What that last price was actually quoted in - may differ from this RFQ's own
        /// currency (a past purchase in USD compared against a fresh AED quote). Null means "not yet
        /// typed"; formatting falls back to "AED" the same way <see cref="Utilities.MoneyFormat"/>
        /// does everywhere else.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedLastPrice))]
        public partial string? LastPriceCurrency { get; set; }

        /// <summary>What the vendor said instead of a price - "Regret", "No bid", "Item discontinued".
        /// Deliberately not a number and never part of any total: it prints where the dash would
        /// otherwise print, so a blank cell in the comparison says why it is blank.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedQuotedUnitPrice))]
        [NotifyPropertyChangedFor(nameof(HasPriceNote))]
        public partial string PriceNote { get; set; } = string.Empty;

        public bool HasPriceNote => !string.IsNullOrWhiteSpace(PriceNote);

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
                return HasPriceNote ? PriceNote.Trim() : string.Empty;
            }
        }

        /// <summary>What the Unit Rate box reads and writes. A number is a price; anything else is
        /// kept verbatim as the note, with the price left empty so no total can pick it up.
        ///
        /// The setter does NOT raise its own change notification: the box would then be rewritten
        /// mid-word with the parsed value and the caret would jump to the end. Changes from
        /// elsewhere - a paste, a reload - do refresh it, through the two properties below.</summary>
        public string QuotedUnitPriceText
        {
            get => QuotedUnitPrice.HasValue
                ? QuotedUnitPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)
                : (PriceNote ?? string.Empty);
            set
            {
                var text = (value ?? string.Empty).Trim();
                _suppressPriceTextEcho = true;
                try
                {
                    if (text.Length == 0)
                    {
                        QuotedUnitPrice = null;
                        PriceNote = string.Empty;
                    }
                    else if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed)
                          || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                    {
                        QuotedUnitPrice = parsed;
                        PriceNote = string.Empty;
                    }
                    else
                    {
                        QuotedUnitPrice = null;
                        PriceNote = text;
                    }
                }
                finally
                {
                    _suppressPriceTextEcho = false;
                }
            }
        }

        private bool _suppressPriceTextEcho;

        partial void OnQuotedUnitPriceChanged(decimal? value) => RaisePriceTextChanged();
        partial void OnPriceNoteChanged(string value) => RaisePriceTextChanged();

        private void RaisePriceTextChanged()
        {
            if (_suppressPriceTextEcho) return;
            OnPropertyChanged(nameof(QuotedUnitPriceText));
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
                    return Utilities.MoneyFormat.Format(LastPriceCurrency, LastPrice.Value);
                }
                return string.Empty;
            }
        }

        /// <summary>What the Last Price box reads and writes. Typing a currency alongside the number
        /// - "86.75 usd", "$86.75", "USD 86.75" - sets <see cref="LastPriceCurrency"/> too; a bare
        /// number leaves whatever currency the row already had. Mirrors
        /// <see cref="QuotedUnitPriceText"/>: no self-echo, so the caret never jumps mid-type.
        ///
        /// Always shows the currency, not just the number - reopening this row after months away
        /// with a bare "86.75" left no way to tell what it was actually priced in.</summary>
        public string LastPriceText
        {
            get => LastPrice.HasValue
                ? $"{LastPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)} {(string.IsNullOrWhiteSpace(LastPriceCurrency) ? "AED" : LastPriceCurrency.ToUpperInvariant())}"
                : string.Empty;
            set
            {
                _suppressLastPriceTextEcho = true;
                try
                {
                    var (amount, currency) = Utilities.SmartPriceParser.Parse(value, LastPriceCurrency ?? "AED");
                    LastPrice = amount;
                    LastPriceCurrency = currency;
                }
                finally
                {
                    _suppressLastPriceTextEcho = false;
                }
            }
        }

        private bool _suppressLastPriceTextEcho;

        partial void OnLastPriceChanged(decimal? value) => RaiseLastPriceTextChanged();
        partial void OnLastPriceCurrencyChanged(string? value) => RaiseLastPriceTextChanged();

        private void RaiseLastPriceTextChanged()
        {
            if (_suppressLastPriceTextEcho) return;
            OnPropertyChanged(nameof(LastPriceText));
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
