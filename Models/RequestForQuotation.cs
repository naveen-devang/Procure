using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class RequestForQuotation : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PrId { get; set; }

        [ObservableProperty]
        public partial string RfqNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Vendor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Status { get; set; } = RfqStatus.Sent;

        [ObservableProperty]
        public partial DateTime? SentDate { get; set; }

        [ObservableProperty]
        public partial DateTime? QuoteReceivedDate { get; set; }

        [ObservableProperty]
        public partial string Currency { get; set; } = "AED";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BaseAmount))]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(TotalLandedCost))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedBaseAmount))]
        [NotifyPropertyChangedFor(nameof(FinancialBreakdownSummary))]
        public partial decimal? QuoteAmount { get; set; }

        [ObservableProperty]
        public partial string PaymentTerms { get; set; } = "30 Days Net";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(TotalLandedCost))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedVatAmount))]
        [NotifyPropertyChangedFor(nameof(FinancialBreakdownSummary))]
        [NotifyPropertyChangedFor(nameof(VatBadgeText))]
        public partial string VatType { get; set; } = "5%"; // 5%, RC, V0

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FreightAmount))]
        [NotifyPropertyChangedFor(nameof(NetTaxableAmount))]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(TotalLandedCost))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedFreight))]
        [NotifyPropertyChangedFor(nameof(FinancialBreakdownSummary))]
        [NotifyPropertyChangedFor(nameof(FormattedFreightBadge))]
        [NotifyPropertyChangedFor(nameof(HasFreight))]
        public partial decimal? Freight { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OtherChargesAmount))]
        [NotifyPropertyChangedFor(nameof(NetTaxableAmount))]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(TotalLandedCost))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedOtherCharges))]
        [NotifyPropertyChangedFor(nameof(FinancialBreakdownSummary))]
        [NotifyPropertyChangedFor(nameof(FormattedOtherChargesBadge))]
        [NotifyPropertyChangedFor(nameof(HasOtherCharges))]
        public partial decimal? OtherCharges { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DiscountAmount))]
        [NotifyPropertyChangedFor(nameof(NetTaxableAmount))]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(TotalLandedCost))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedDiscount))]
        [NotifyPropertyChangedFor(nameof(FinancialBreakdownSummary))]
        [NotifyPropertyChangedFor(nameof(HasDiscount))]
        public partial decimal? Discount { get; set; }

        [ObservableProperty]
        public partial string Incoterms { get; set; } = "DDP";

        [ObservableProperty]
        public partial string DeliveryLeadTime { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Warranty { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTechnicalApproval))]
        [NotifyPropertyChangedFor(nameof(IsTechnicallyApproved))]
        [NotifyPropertyChangedFor(nameof(IsTechnicallyNotApproved))]
        public partial string TechnicalApproval { get; set; } = string.Empty;

        public bool HasTechnicalApproval => !string.IsNullOrWhiteSpace(TechnicalApproval);

        public bool IsTechnicallyApproved => string.Equals(TechnicalApproval, "Approved", StringComparison.OrdinalIgnoreCase);

        public bool IsTechnicallyNotApproved => string.Equals(TechnicalApproval, "Not Approved", StringComparison.OrdinalIgnoreCase);

        [ObservableProperty]
        public partial string SharedPrs { get; set; } = string.Empty;

        [ObservableProperty]
        public partial ObservableCollection<RfqItem> Items { get; set; } = new();

        public int PricedItemsCount => Items?.Count(i => i.IsQuoted && (i.QuotedUnitPrice.HasValue && i.QuotedUnitPrice.Value > 0 || i.LineTotal > 0)) ?? 0;
        public int QuotedItemsCount => Items?.Count(i => i.IsQuoted) ?? 0;
        public int TotalItemsCount => Items?.Count ?? 0;

        public bool HasLineItems => Items != null && Items.Count > 0;

        public bool IsFullQuote => HasLineItems && TotalItemsCount > 0 && PricedItemsCount == TotalItemsCount;
        public bool IsPartialQuote => HasLineItems && PricedItemsCount > 0 && PricedItemsCount < TotalItemsCount;

        public bool HasQuoteCompletenessBadge => HasLineItems && IsQuoteReceived && PricedItemsCount > 0;

        public string QuoteCompletenessBadge
        {
            get
            {
                if (!HasLineItems || !IsQuoteReceived || PricedItemsCount == 0) return string.Empty;
                if (IsPartialQuote) return $"Partial ({PricedItemsCount}/{TotalItemsCount} items)";
                return $"Full Quote ({PricedItemsCount} items)";
            }
        }

        public bool IsSharedRfq => !string.IsNullOrWhiteSpace(SharedPrs);

        public bool IsQuoteReceived => Status == RfqStatus.QuoteReceived || QuoteReceivedDate.HasValue || (QuoteAmount.HasValue && QuoteAmount.Value > 0) || (HasLineItems && PricedItemsCount > 0 && BaseAmount > 0);

        public bool HasFreight => Freight.HasValue && Freight.Value > 0;

        public bool HasOtherCharges => OtherCharges.HasValue && OtherCharges.Value > 0;

        public bool HasDiscount => Discount.HasValue && Discount.Value > 0;

        public decimal BaseAmount
        {
            get
            {
                if (HasLineItems)
                {
                    var itemsSum = Items.Where(i => i.IsQuoted).Sum(i => i.LineTotal);
                    if (itemsSum > 0 || PricedItemsCount > 0)
                        return itemsSum;
                }
                return QuoteAmount ?? 0m;
            }
        }

        public decimal FreightAmount => Freight ?? 0m;

        public decimal OtherChargesAmount => OtherCharges ?? 0m;

        public decimal DiscountAmount => Discount ?? 0m;

        public decimal NetTaxableAmount => Math.Max(0m, (BaseAmount + FreightAmount + OtherChargesAmount) - DiscountAmount);

        public decimal EffectiveVatAmount => (VatType == "5%") ? NetTaxableAmount * 0.05m : 0m;

        public decimal DisplayTotalAmount => NetTaxableAmount + EffectiveVatAmount;

        public decimal TotalLandedCost => DisplayTotalAmount;

        public string FormattedDisplayAmount
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return (DisplayTotalAmount % 1 == 0) ? $"{cur} {DisplayTotalAmount:N0}" : $"{cur} {DisplayTotalAmount:N2}";
            }
        }

        public string FormattedBaseAmount
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return (BaseAmount % 1 == 0) ? $"{cur} {BaseAmount:N0}" : $"{cur} {BaseAmount:N2}";
            }
        }

        public string FormattedFreight
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return (FreightAmount % 1 == 0) ? $"{cur} {FreightAmount:N0}" : $"{cur} {FreightAmount:N2}";
            }
        }

        public string FormattedOtherCharges
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return (OtherChargesAmount % 1 == 0) ? $"{cur} {OtherChargesAmount:N0}" : $"{cur} {OtherChargesAmount:N2}";
            }
        }

        public string FormattedDiscount
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return (DiscountAmount % 1 == 0) ? $"{cur} {DiscountAmount:N0}" : $"{cur} {DiscountAmount:N2}";
            }
        }

        public string FormattedVatAmount
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return (EffectiveVatAmount % 1 == 0) ? $"{cur} {EffectiveVatAmount:N0}" : $"{cur} {EffectiveVatAmount:N2}";
            }
        }

        public string FinancialBreakdownSummary
        {
            get
            {
                if (BaseAmount <= 0)
                    return "Quote pending from vendor";

                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                var baseStr = (BaseAmount % 1 == 0) ? $"{cur} {BaseAmount:N0}" : $"{cur} {BaseAmount:N2}";
                var parts = new List<string> { $"Base: {baseStr}" };

                if (HasFreight)
                {
                    var frStr = (FreightAmount % 1 == 0) ? $"{cur} {FreightAmount:N0}" : $"{cur} {FreightAmount:N2}";
                    parts.Add($"Freight: {frStr}");
                }

                if (HasOtherCharges)
                {
                    var othStr = (OtherChargesAmount % 1 == 0) ? $"{cur} {OtherChargesAmount:N0}" : $"{cur} {OtherChargesAmount:N2}";
                    parts.Add($"Other: {othStr}");
                }

                if (HasDiscount)
                {
                    var discStr = (DiscountAmount % 1 == 0) ? $"{cur} {DiscountAmount:N0}" : $"{cur} {DiscountAmount:N2}";
                    parts.Add($"Discount: -{discStr}");
                }

                if (VatType == "5%")
                {
                    var vatStr = (EffectiveVatAmount % 1 == 0) ? $"{cur} {EffectiveVatAmount:N0}" : $"{cur} {EffectiveVatAmount:N2}";
                    parts.Add($"5% VAT: {vatStr}");
                }
                else if (VatType == "RC")
                    parts.Add("Reverse Charge (0% Tax)");
                else
                    parts.Add("Zero-Rated (0% Tax)");

                return string.Join("  •  ", parts);
            }
        }

        public bool HasPaymentTerms => !string.IsNullOrWhiteSpace(PaymentTerms);

        public bool HasDeliveryLeadTime => !string.IsNullOrWhiteSpace(DeliveryLeadTime);

        public bool HasIncoterms => !string.IsNullOrWhiteSpace(Incoterms);

        public bool HasVatBreakdown => VatType == "5%" && BaseAmount > 0;

        public string VatBadgeText => VatType switch
        {
            "5%" => "5% VAT",
            "RC" => "VAT: RC",
            "V0" => "Zero VAT",
            _ => string.IsNullOrWhiteSpace(VatType) ? "5% VAT" : $"VAT: {VatType}"
        };

        public string FormattedFreightBadge => HasFreight
            ? $"+ {(string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency)} {FreightAmount:N0} Freight"
            : string.Empty;

        public string FormattedOtherChargesBadge => HasOtherCharges
            ? $"+ {(string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency)} {OtherChargesAmount:N0} Other"
            : string.Empty;

        public string FormattedLeadTimeBadge => HasDeliveryLeadTime
            ? $"Lead: {DeliveryLeadTime.Trim()}"
            : string.Empty;

        public string FormattedPaymentTermsBadge => HasPaymentTerms
            ? $"Terms: {PaymentTerms.Trim()}"
            : string.Empty;

        public string FormattedVatBreakdownInline
        {
            get
            {
                if (!HasVatBreakdown) return string.Empty;
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return $"(Base: {cur} {BaseAmount:N0} + VAT: {cur} {EffectiveVatAmount:N0})";
            }
        }

        public bool HasCommercialDetails => HasPaymentTerms ||
                                            HasIncoterms ||
                                            HasDeliveryLeadTime ||
                                            !string.IsNullOrWhiteSpace(VatType) ||
                                            HasFreight ||
                                            HasOtherCharges;

        public void NotifyCalculationsChanged()
        {
            OnPropertyChanged(nameof(BaseAmount));
            OnPropertyChanged(nameof(FreightAmount));
            OnPropertyChanged(nameof(OtherChargesAmount));
            OnPropertyChanged(nameof(DiscountAmount));
            OnPropertyChanged(nameof(NetTaxableAmount));
            OnPropertyChanged(nameof(EffectiveVatAmount));
            OnPropertyChanged(nameof(DisplayTotalAmount));
            OnPropertyChanged(nameof(TotalLandedCost));
            OnPropertyChanged(nameof(FormattedDisplayAmount));
            OnPropertyChanged(nameof(FormattedBaseAmount));
            OnPropertyChanged(nameof(FormattedFreight));
            OnPropertyChanged(nameof(FormattedOtherCharges));
            OnPropertyChanged(nameof(FormattedDiscount));
            OnPropertyChanged(nameof(FormattedVatAmount));
            OnPropertyChanged(nameof(FinancialBreakdownSummary));
            OnPropertyChanged(nameof(HasFreight));
            OnPropertyChanged(nameof(HasOtherCharges));
            OnPropertyChanged(nameof(HasDiscount));
            OnPropertyChanged(nameof(FormattedFreightBadge));
            OnPropertyChanged(nameof(FormattedOtherChargesBadge));
            OnPropertyChanged(nameof(QuotedItemsCount));
            OnPropertyChanged(nameof(TotalItemsCount));
            OnPropertyChanged(nameof(IsPartialQuote));
            OnPropertyChanged(nameof(IsFullQuote));
            OnPropertyChanged(nameof(QuoteCompletenessBadge));
            OnPropertyChanged(nameof(HasQuoteCompletenessBadge));
            OnPropertyChanged(nameof(IsQuoteReceived));
            OnPropertyChanged(nameof(Warranty));
            OnPropertyChanged(nameof(TechnicalApproval));
            OnPropertyChanged(nameof(HasTechnicalApproval));
            OnPropertyChanged(nameof(IsTechnicallyApproved));
            OnPropertyChanged(nameof(IsTechnicallyNotApproved));
            // Chip visibility/text in the expanded panel; without these an RFQ edit leaves the
            // shared/payment-terms/lead-time chips showing pre-edit values until a full rebuild.
            OnPropertyChanged(nameof(IsSharedRfq));
            OnPropertyChanged(nameof(HasPaymentTerms));
            OnPropertyChanged(nameof(FormattedPaymentTermsBadge));
            OnPropertyChanged(nameof(HasDeliveryLeadTime));
            OnPropertyChanged(nameof(FormattedLeadTimeBadge));
            OnPropertyChanged(nameof(HasCommercialDetails));
        }
    }
}
