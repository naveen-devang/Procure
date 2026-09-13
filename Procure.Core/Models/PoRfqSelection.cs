using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Procure.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PoRfqItemSelection : ObservableModel, Procure.Utilities.IPrLine, Procure.Utilities.IQuantified
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? RfqItemId { get; set; }
        public Guid? PrItemId { get; set; }

        [ObservableProperty]
        public partial string ItemName { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedQuantity))]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        [NotifyPropertyChangedFor(nameof(TotalAllocated))]
        [NotifyPropertyChangedFor(nameof(PendingQuantity))]
        [NotifyPropertyChangedFor(nameof(IsFullyAllocated))]
        [NotifyPropertyChangedFor(nameof(IsPendingAllocation))]
        [NotifyPropertyChangedFor(nameof(IsOverAllocated))]
        [NotifyPropertyChangedFor(nameof(OverAllocatedQuantity))]
        [NotifyPropertyChangedFor(nameof(AllocationStatusText))]
        public partial decimal Quantity { get; set; } = 1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedQuantity))]
        public partial string Unit { get; set; } = "pcs";

        /// <summary>Contracts this line's quantity travels under, when the order is per-line.
        /// Normally one; two when the quantity is split. Empty in whole-order mode.</summary>
        public ObservableCollection<PoItemTransport> Transports { get; } = new();

        public decimal TransportTotal
        {
            get { var sum = 0m; foreach (var t in Transports) sum += t.Total; return sum; }
        }

        public decimal AllocatedQuantity
        {
            get { var sum = 0m; foreach (var t in Transports) sum += t.Quantity; return sum; }
        }

        public decimal UnallocatedQuantity => Quantity - AllocatedQuantity;

        public bool IsOverAllocatedTransport => UnallocatedQuantity < 0m;

        /// <summary>The chip on the line: the contract when there is one, a count when the quantity
        /// is split, a prompt when nothing is set.</summary>
        public string TransportSummary => Transports.Count switch
        {
            0 => "Set transport",
            1 => string.IsNullOrWhiteSpace(Transports[0].ContractLabel) ? "Set transport" : Transports[0].ContractLabel,
            var n => $"{n} contracts",
        };

        public bool HasTransport => Transports.Count > 0
            && !string.IsNullOrWhiteSpace(Transports[0].ContractLabel);

        /// <summary>Running total under the open panel - the thing that says whether every unit has
        /// a way to travel.</summary>
        public string TransportAllocationText
        {
            get
            {
                var q = Quantity.ToString("G29", CultureInfo.InvariantCulture);
                var a = AllocatedQuantity.ToString("G29", CultureInfo.InvariantCulture);
                if (UnallocatedQuantity > 0m)
                    return $"{a} of {q} allocated - {UnallocatedQuantity.ToString("G29", CultureInfo.InvariantCulture)} with no contract";
                if (UnallocatedQuantity < 0m)
                    return $"{a} of {q} allocated - {(-UnallocatedQuantity).ToString("G29", CultureInfo.InvariantCulture)} more than ordered";
                return $"{a} of {q} allocated";
            }
        }

        [ObservableProperty]
        public partial bool IsTransportExpanded { get; set; }

        /// <summary>Mirrors the order's transport mode. Set by PoRfqSelection whenever the mode
        /// changes, because a line's DataTemplate cannot bind up to the card that owns it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TransportColumnWidth))]
        public partial bool IsLineTransport { get; set; }

        /// <summary>Matches the card's, so a row's cells line up with the headings above them.</summary>
        public double TransportColumnWidth => IsLineTransport ? 150d : 0d;

        /// <summary>Called after any allocation edit: the totals above are plain computed properties
        /// over a collection, so nothing tells the UI on its own.</summary>
        public void NotifyTransportChanged()
        {
            OnPropertyChanged(nameof(TransportTotal));
            OnPropertyChanged(nameof(AllocatedQuantity));
            OnPropertyChanged(nameof(UnallocatedQuantity));
            OnPropertyChanged(nameof(IsOverAllocatedTransport));
            OnPropertyChanged(nameof(TransportSummary));
            OnPropertyChanged(nameof(HasTransport));
            OnPropertyChanged(nameof(TransportAllocationText));
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        [NotifyPropertyChangedFor(nameof(TotalAllocated))]
        [NotifyPropertyChangedFor(nameof(PendingQuantity))]
        [NotifyPropertyChangedFor(nameof(IsFullyAllocated))]
        [NotifyPropertyChangedFor(nameof(IsPendingAllocation))]
        [NotifyPropertyChangedFor(nameof(IsOverAllocated))]
        [NotifyPropertyChangedFor(nameof(OverAllocatedQuantity))]
        [NotifyPropertyChangedFor(nameof(AllocationStatusText))]
        public partial bool IsSelected { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedQuotedUnitPrice))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        public partial decimal? QuotedUnitPrice { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LineTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedDiscount))]
        [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
        public partial decimal? Discount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedLastPrice))]
        public partial decimal? LastPrice { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MaxAllowedQuantity))]
        [NotifyPropertyChangedFor(nameof(TotalAllocated))]
        [NotifyPropertyChangedFor(nameof(PendingQuantity))]
        [NotifyPropertyChangedFor(nameof(IsFullyAllocated))]
        [NotifyPropertyChangedFor(nameof(IsPendingAllocation))]
        [NotifyPropertyChangedFor(nameof(IsOverAllocated))]
        [NotifyPropertyChangedFor(nameof(OverAllocatedQuantity))]
        [NotifyPropertyChangedFor(nameof(AllocationStatusText))]
        public partial decimal PrTargetQuantity { get; set; } = 1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MaxAllowedQuantity))]
        [NotifyPropertyChangedFor(nameof(TotalAllocated))]
        [NotifyPropertyChangedFor(nameof(PendingQuantity))]
        [NotifyPropertyChangedFor(nameof(IsFullyAllocated))]
        [NotifyPropertyChangedFor(nameof(IsPendingAllocation))]
        [NotifyPropertyChangedFor(nameof(IsOverAllocated))]
        [NotifyPropertyChangedFor(nameof(OverAllocatedQuantity))]
        [NotifyPropertyChangedFor(nameof(AllocationStatusText))]
        public partial decimal OtherPosOrderedQuantity { get; set; } = 0;

        /// <summary>What the OTHER rows open in this same PO window already allocate to this row's PR
        /// line - sibling rows on this card and every row on every other selected vendor card.
        ///
        /// Without it a row only ever compared itself against saved POs, so a merged PR quoted on two
        /// lines showed "12/12 Fully Allocated" on both rows while the banner above them said 24
        /// against a target of 12. Kept separate from <see cref="OtherPosOrderedQuantity"/> so the
        /// status text can still say which quantity came from where. Maintained live by
        /// PrListPageModel.RecalculatePoModalTotals.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MaxAllowedQuantity))]
        [NotifyPropertyChangedFor(nameof(TotalAllocated))]
        [NotifyPropertyChangedFor(nameof(PendingQuantity))]
        [NotifyPropertyChangedFor(nameof(IsFullyAllocated))]
        [NotifyPropertyChangedFor(nameof(IsPendingAllocation))]
        [NotifyPropertyChangedFor(nameof(IsOverAllocated))]
        [NotifyPropertyChangedFor(nameof(OverAllocatedQuantity))]
        [NotifyPropertyChangedFor(nameof(AllocationStatusText))]
        public partial decimal OtherRowsQuantity { get; set; } = 0;

        public decimal AlreadyAllocatedElsewhere => OtherPosOrderedQuantity + OtherRowsQuantity;

        public decimal TotalAllocated => AlreadyAllocatedElsewhere + (IsSelected ? Quantity : 0m);

        public decimal MaxAllowedQuantity => Math.Max(0m, PrTargetQuantity - AlreadyAllocatedElsewhere);

        public bool IsOverAllocated => TotalAllocated > PrTargetQuantity && PrTargetQuantity > 0;

        public bool IsFullyAllocated => TotalAllocated == PrTargetQuantity && PrTargetQuantity > 0;

        public bool IsPendingAllocation => TotalAllocated < PrTargetQuantity && PrTargetQuantity > 0;

        public decimal OverAllocatedQuantity => IsOverAllocated ? (TotalAllocated - PrTargetQuantity) : 0m;

        public decimal PendingQuantity => Math.Max(0m, PrTargetQuantity - TotalAllocated);

        /// <summary>This row prices something the requisition does not list - a line typed straight
        /// into the quote and kept as an extra. It has no PR target to be measured against, so
        /// saying "Fully Allocated" about it would be inventing one.</summary>
        public bool IsUnbudgeted { get; set; }

        public string AllocationStatusText
        {
            get
            {
                var unitStr = string.IsNullOrWhiteSpace(Unit) ? "pcs" : Unit;

                if (IsUnbudgeted)
                {
                    return $"Not on the requisition • extra line ({Quantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr})";
                }

                var thisPoQty = IsSelected ? Quantity : 0m;
                var target = $"PR Target: {PrTargetQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr}";

                // One breakdown, built once, so every branch below reports the same three sources.
                // The branches used to spell out their own subsets and quietly omitted whatever the
                // rest of the window had already allocated.
                var parts = new List<string>(3);
                if (OtherPosOrderedQuantity > 0) parts.Add($"Other POs: {OtherPosOrderedQuantity.ToString("G29", CultureInfo.InvariantCulture)}");
                if (OtherRowsQuantity > 0) parts.Add($"Other lines here: {OtherRowsQuantity.ToString("G29", CultureInfo.InvariantCulture)}");
                if (thisPoQty > 0) parts.Add($"This PO: {thisPoQty.ToString("G29", CultureInfo.InvariantCulture)}");
                var breakdown = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : string.Empty;

                if (IsOverAllocated)
                {
                    return $"Exceeds PR target by {OverAllocatedQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} • {target}{breakdown}";
                }

                if (IsFullyAllocated)
                {
                    return $"{target}{breakdown} • Fully Allocated";
                }

                var pending = PendingQuantity;
                if (parts.Count == 0)
                {
                    return $"{target} • {pending.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} Pending (Unallocated)";
                }

                return $"{target}{breakdown} • {pending.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} Pending";
            }
        }

        public decimal LineTotal => (IsSelected && QuotedUnitPrice.HasValue && QuotedUnitPrice.Value > 0)
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
                if (IsSelected && QuotedUnitPrice.HasValue && QuotedUnitPrice.Value > 0)
                {
                    return LineTotal.ToString("N2", CultureInfo.InvariantCulture);
                }
                return "-";
            }
        }

        public Action? OnPriceOrSelectionChanged { get; set; }

        partial void OnIsSelectedChanged(bool value) => OnPriceOrSelectionChanged?.Invoke();
        partial void OnQuotedUnitPriceChanged(decimal? value) => OnPriceOrSelectionChanged?.Invoke();
        partial void OnDiscountChanged(decimal? value) => OnPriceOrSelectionChanged?.Invoke();
        partial void OnQuantityChanged(decimal value) => OnPriceOrSelectionChanged?.Invoke();
    }

    public partial class PoRfqSelection : ObservableModel
    {
        public RequestForQuotation? Rfq { get; }
        public Guid? EditingPoId { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryTitle))]
        public partial bool IsSelected { get; set; } = true;

        [ObservableProperty]
        public partial string PoNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Vendor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Currency { get; set; } = "AED";

        [ObservableProperty]
        public partial ObservableCollection<PoRfqItemSelection> Items { get; set; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        public partial string VatType { get; set; } = "5%";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FreightAmount))]
        [NotifyPropertyChangedFor(nameof(NetTaxableAmount))]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        public partial decimal? Freight { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OtherChargesAmount))]
        [NotifyPropertyChangedFor(nameof(NetTaxableAmount))]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        public partial decimal? OtherCharges { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OverallDiscountAmount))]
        [NotifyPropertyChangedFor(nameof(NetTaxableAmount))]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        public partial decimal? OverallDiscount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BaseAmount))]
        [NotifyPropertyChangedFor(nameof(NetTaxableAmount))]
        [NotifyPropertyChangedFor(nameof(EffectiveVatAmount))]
        [NotifyPropertyChangedFor(nameof(DisplayTotalAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedDisplayAmount))]
        [NotifyPropertyChangedFor(nameof(FormattedBaseAmount))]
        public partial decimal? CustomBaseAmount { get; set; }

        // Transport details, Raw Material POs only. Kept out of BaseAmount/NetTaxableAmount/
        // DisplayTotalAmount entirely - haulage is tracked here, never folded into the PO's total.
        /// <summary>Raw Material or Packing Material: the two types that carry transport. Packing
        /// was excluded before, so a packing PO had nowhere to put a contract.</summary>
        [ObservableProperty]
        public partial bool IsRawMaterial { get; set; }

        internal static bool IsTransportType(string? prType) =>
            string.Equals(prType, ProcurementPrType.RawMaterial, StringComparison.OrdinalIgnoreCase)
            || string.Equals(prType, ProcurementPrType.PackingMaterial, StringComparison.OrdinalIgnoreCase);

        /// <summary>"Order" - one contract for the whole PO, in the three properties below, which
        /// is the default and how every PO worked before. "Line" - each line carries its own, in
        /// PoRfqItemSelection.Transports.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLineTransport))]
        [NotifyPropertyChangedFor(nameof(IsOrderTransport))]
        [NotifyPropertyChangedFor(nameof(TransportColumnWidth))]
        [NotifyPropertyChangedFor(nameof(FormattedTransportTotal))]
        public partial string TransportMode { get; set; } = TransportModes.Order;

        public bool IsLineTransport => TransportMode == TransportModes.Line;
        public bool IsOrderTransport => !IsLineTransport;

        /// <summary>The Transport column's width, on the header and on every row alike. Auto sized
        /// to the heading in one and to a wider chip in the other, so the cells drifted out of line
        /// with their headings. Zero in whole-order mode, where the column does not exist.</summary>
        public double TransportColumnWidth => IsLineTransport ? 150d : 0d;

        [ObservableProperty]
        public partial string? TransportContractNumber { get; set; }

        [ObservableProperty]
        public partial string? TransporterName { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TransportTotal))]
        [NotifyPropertyChangedFor(nameof(FormattedTransportTotal))]
        public partial decimal? TransportRatePerUnit { get; set; }

        public decimal OrderedQuantity => Items?.Where(i => i.IsSelected).Sum(i => i.Quantity) ?? 0m;

        /// <summary>Whole-order: rate x everything ordered. Per-line: the sum of each line's
        /// allocations. One accessor so no caller has to know which mode the order is in.</summary>
        public decimal? TransportTotal
        {
            get
            {
                if (IsLineTransport)
                {
                    var sum = 0m;
                    foreach (var item in Items ?? Enumerable.Empty<PoRfqItemSelection>())
                        if (item.IsSelected) sum += item.TransportTotal;
                    return sum;
                }
                return TransportRatePerUnit.HasValue ? TransportRatePerUnit.Value * OrderedQuantity : null;
            }
        }

        /// <summary>Lines that are short of a contract for some of their quantity. Shown as a
        /// warning rather than blocking the save - transport is often arranged after the PO.</summary>
        public int UnallocatedLineCount
        {
            get
            {
                var n = 0;
                if (!IsLineTransport) return 0;
                foreach (var item in Items ?? Enumerable.Empty<PoRfqItemSelection>())
                    if (item.IsSelected && item.UnallocatedQuantity > 0m) n++;
                return n;
            }
        }

        public bool HasUnallocatedLines => UnallocatedLineCount > 0;

        public string UnallocatedLineText => UnallocatedLineCount == 1
            ? "1 line has quantity with no transport contract"
            : $"{UnallocatedLineCount} lines have quantity with no transport contract";

        /// <summary>Switching mode keeps what was entered: going per-line seeds every selected line
        /// with the order's contract so nothing is retyped, and going back to whole-order drops the
        /// per-line rows (the caller warns first when the lines disagree).</summary>
        public void SwitchTransportMode(string mode)
        {
            if (mode == TransportMode) return;

            if (mode == TransportModes.Line)
            {
                foreach (var item in Items ?? Enumerable.Empty<PoRfqItemSelection>())
                {
                    if (!item.IsSelected || item.Transports.Count > 0) continue;
                    item.Transports.Add(new PoItemTransport
                    {
                        Quantity = item.Quantity,
                        ContractNumber = TransportContractNumber,
                        TransporterName = TransporterName,
                        RatePerUnit = TransportRatePerUnit,
                        Currency = Currency ?? "AED",
                    });
                    item.NotifyTransportChanged();
                }
            }
            else
            {
                foreach (var item in Items ?? Enumerable.Empty<PoRfqItemSelection>())
                {
                    item.Transports.Clear();
                    item.NotifyTransportChanged();
                }
            }

            TransportMode = mode;
            foreach (var item in Items ?? Enumerable.Empty<PoRfqItemSelection>())
                item.IsLineTransport = mode == TransportModes.Line;
            NotifyCalculationsChanged();
        }

        /// <summary>The distinct contracts across the lines, for the warning shown when collapsing
        /// a per-line order back to one contract.</summary>
        public List<string> DistinctLineContracts()
        {
            var seen = new List<string>();
            foreach (var item in Items ?? Enumerable.Empty<PoRfqItemSelection>())
            {
                if (!item.IsSelected) continue;
                foreach (var t in item.Transports)
                {
                    var name = t.ContractNumber?.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!seen.Contains(name, StringComparer.OrdinalIgnoreCase)) seen.Add(name);
                }
            }
            return seen;
        }

        public string FormattedTransportTotal
        {
            get
            {
                // Empty read as a heading with no field under it in the PO dialog's Transport
                // block; no rate entered is a transport total of zero, so say so.
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return $"{cur} {(TransportTotal ?? 0m):N2}";
            }
        }

        partial void OnFreightChanged(decimal? value) => NotifyCalculationsChanged();
        partial void OnOtherChargesChanged(decimal? value) => NotifyCalculationsChanged();
        partial void OnOverallDiscountChanged(decimal? value) => NotifyCalculationsChanged();

        private string _lastValidVatType = "5%";

        partial void OnVatTypeChanged(string value)
        {
            // A Picker whose ItemsSource resolves after SelectedItem pushes null through the
            // TwoWay binding; accepting it silently zeroes the VAT in every derived total.
            if (string.IsNullOrWhiteSpace(value))
            {
                VatType = _lastValidVatType;
                return;
            }
            _lastValidVatType = value;
            NotifyCalculationsChanged();
        }

        partial void OnCustomBaseAmountChanged(decimal? value) => NotifyCalculationsChanged();

        public bool HasItems => Items != null && Items.Count > 0;
        public int TotalItemsCount => Items?.Count ?? 0;
        public int SelectedItemsCount => Items?.Count(i => i.IsSelected) ?? 0;
        public bool AllItemsSelected => Items is { Count: > 0 } && Items.All(i => i.IsSelected);
        public int PricedItemsCount => Items?.Count(i => i.IsSelected && i.QuotedUnitPrice.HasValue && i.QuotedUnitPrice.Value > 0) ?? 0;
        public bool HasOverAllocatedItems => Items != null && Items.Any(i => i.IsSelected && i.IsOverAllocated);

        public string SelectedItemsSummary => HasItems
            ? $"{SelectedItemsCount} of {TotalItemsCount} items selected"
            : "Lump Sum Quote";

        public string VendorName => string.IsNullOrWhiteSpace(Vendor) ? "Unnamed Supplier" : Vendor;
        public string RfqNumber => string.IsNullOrWhiteSpace(Rfq?.RfqNo) ? string.Empty : Rfq.RfqNo;
        public bool HasRfqNumber => !string.IsNullOrWhiteSpace(RfqNumber);

        public string SummaryTitle
        {
            get
            {
                var money = Procure.Utilities.MoneyFormat.Format(Currency, DisplayTotalAmount);
                if (!string.IsNullOrWhiteSpace(RfqNumber))
                    return $"{VendorName} • {money} ({RfqNumber})";
                return $"{VendorName} • {money}";
            }
        }

        public decimal BaseAmount
        {
            get
            {
                if (CustomBaseAmount.HasValue)
                {
                    return CustomBaseAmount.Value;
                }
                if (HasItems)
                {
                    return Items.Where(i => i.IsSelected).Sum(i => i.LineTotal);
                }
                return Rfq?.QuoteAmount ?? 0m;
            }
        }

        public decimal FreightAmount => Freight ?? 0m;
        public decimal OtherChargesAmount => OtherCharges ?? 0m;
        public decimal OverallDiscountAmount => OverallDiscount ?? 0m;

        public decimal NetTaxableAmount => Math.Max(0m, (BaseAmount + FreightAmount + OtherChargesAmount) - OverallDiscountAmount);

        public decimal EffectiveVatAmount => (VatType == "5%") ? NetTaxableAmount * 0.05m : 0m;

        public decimal DisplayTotalAmount => NetTaxableAmount + EffectiveVatAmount;

        public string FormattedDisplayAmount
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return $"{cur} {DisplayTotalAmount:N2}";
            }
        }

        public string FormattedBaseAmount
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return $"{cur} {BaseAmount:N2}";
            }
        }

        public string FormattedVatAmount
        {
            get
            {
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                return $"{cur} {EffectiveVatAmount:N2}";
            }
        }

        public string PaymentTerms => Rfq?.PaymentTerms ?? string.Empty;
        public string Incoterms => Rfq?.Incoterms ?? string.Empty;
        public string DeliveryLeadTime => Rfq?.DeliveryLeadTime ?? string.Empty;
        public string Warranty => Rfq?.Warranty ?? string.Empty;
        public string TechnicalApproval => Rfq?.TechnicalApproval ?? string.Empty;

        public bool HasTechnicalApproval => !string.IsNullOrWhiteSpace(TechnicalApproval);
        public bool IsTechnicallyApproved => string.Equals(TechnicalApproval, "Approved", StringComparison.OrdinalIgnoreCase);

        public Action? OnTotalsRecalculated { get; set; }

        public PoRfqSelection(RequestForQuotation rfq, PurchaseRequisition? pr = null, bool isSelected = true)
        {
            Rfq = rfq ?? throw new ArgumentNullException(nameof(rfq));
            IsSelected = isSelected;
            Vendor = rfq.Vendor ?? string.Empty;
            Currency = string.IsNullOrWhiteSpace(rfq.Currency) ? "AED" : rfq.Currency;
            VatType = string.IsNullOrWhiteSpace(rfq.VatType) ? "5%" : rfq.VatType;
            Freight = rfq.Freight;
            OtherCharges = rfq.OtherCharges;
            OverallDiscount = rfq.Discount;
            IsRawMaterial = IsTransportType(pr?.PrType);

            // One resolution pass for the whole quote, so two lines naming the same item claim two
            // different PR lines instead of both latching onto the first.
            var prMap = PrLineMatcher.Map(rfq.Items, pr?.Items);
            var alreadyOrdered = PrLineMatcher.OrderedQuantities(pr?.Items, pr?.Pos);
            // Rows resolved earlier in this loop have already spoken for part of their PR line.
            var takenHere = new Dictionary<Guid, decimal>();

            if (rfq.Items != null && rfq.Items.Count > 0)
            {
                foreach (var rfqItem in rfq.Items)
                {
                    prMap.TryGetValue(rfqItem, out var prItem);
                    var prTarget = prItem?.Quantity ?? rfqItem.Quantity;

                    var otherPoOrdered = prItem != null && alreadyOrdered.TryGetValue(prItem.Id, out var ord) ? ord : 0m;
                    var claimedHere = prItem != null && takenHere.TryGetValue(prItem.Id, out var t) ? t : 0m;

                    var remainingAvail = Math.Max(0m, prTarget - otherPoOrdered - claimedHere);

                    var itemSelection = new PoRfqItemSelection
                    {
                        Id = Guid.NewGuid(),
                        RfqItemId = rfqItem.Id,
                        PrItemId = rfqItem.PrItemId ?? prItem?.Id,
                        ItemName = rfqItem.ItemName,
                        // An item already fully ordered by other POs defaults to 0/unselected;
                        // defaulting to the full RFQ quantity opened the wizard over-allocated.
                        Quantity = Math.Min(rfqItem.Quantity, remainingAvail),
                        PrTargetQuantity = prTarget,
                        OtherPosOrderedQuantity = otherPoOrdered,
                        Unit = rfqItem.Unit,
                        IsSelected = remainingAvail > 0 && rfqItem.IsQuoted && (rfqItem.QuotedUnitPrice.HasValue && rfqItem.QuotedUnitPrice.Value > 0 || rfqItem.LineTotal > 0),
                        QuotedUnitPrice = rfqItem.QuotedUnitPrice,
                        Discount = rfqItem.Discount,
                        LastPrice = rfqItem.LastPrice,
                        OnPriceOrSelectionChanged = OnItemSelectionOrPriceChanged
                    };
                    Items.Add(itemSelection);
                    if (prItem != null) takenHere[prItem.Id] = claimedHere + itemSelection.Quantity;
                }
            }

            // Also add any other PR items not in RFQ so the user can select and price them if desired.
            // Covered-ness is decided by which PR line each row actually resolved to, not by name: two
            // PR lines sharing a name used to look "already covered" the moment one of them was.
            if (pr?.Items != null)
            {
                var covered = new HashSet<Guid>(prMap.Values.Select(p => p.Id));
                foreach (var prItem in pr.Items)
                {
                    if (!covered.Contains(prItem.Id))
                    {
                        var otherPoOrdered = alreadyOrdered.TryGetValue(prItem.Id, out var ord2) ? ord2 : 0m;

                        var remainingAvail = Math.Max(0m, prItem.Quantity - otherPoOrdered);

                        var unquotedItem = new PoRfqItemSelection
                        {
                            Id = Guid.NewGuid(),
                            PrItemId = prItem.Id,
                            ItemName = prItem.ItemName,
                            Quantity = remainingAvail,
                            PrTargetQuantity = prItem.Quantity,
                            OtherPosOrderedQuantity = otherPoOrdered,
                            Unit = prItem.Unit,
                            IsSelected = false,
                            OnPriceOrSelectionChanged = OnItemSelectionOrPriceChanged
                        };
                        Items.Add(unquotedItem);
                    }
                }
            }

            if (Items.Any(i => i.IsSelected))
            {
                CustomBaseAmount = Items.Where(i => i.IsSelected).Sum(i => i.LineTotal);
            }
            else if (rfq.Items != null && rfq.Items.Count > 0)
            {
                // Item-backed quote with nothing selectable (every line already fully ordered by
                // other POs): falling back to the lump QuoteAmount re-offered the vendor's full
                // quote on a card with zero items, bypassing the allocation check.
                CustomBaseAmount = 0m;
                IsSelected = false;
            }
            else
            {
                CustomBaseAmount = rfq.QuoteAmount ?? 0m;
            }
        }

        public PoRfqSelection(PurchaseOrder existingPo, RequestForQuotation? linkedRfq, PurchaseRequisition pr)
        {
            EditingPoId = existingPo.Id;
            Rfq = linkedRfq;
            IsSelected = true;
            PoNo = existingPo.PoNo;
            Vendor = existingPo.Vendor;
            Currency = string.IsNullOrWhiteSpace(existingPo.Currency) ? "AED" : existingPo.Currency;
            VatType = string.IsNullOrWhiteSpace(existingPo.VatType) ? (linkedRfq?.VatType ?? "5%") : existingPo.VatType;
            Freight = existingPo.Freight ?? linkedRfq?.Freight;
            OtherCharges = existingPo.OtherCharges ?? linkedRfq?.OtherCharges;
            OverallDiscount = existingPo.Discount ?? linkedRfq?.Discount;
            CustomBaseAmount = existingPo.BaseAmount;
            // pr is a required argument here (unlike the RFQ constructor's optional one) and the
            // body below indexes into it, so the null-conditional was only ever confusing the
            // compiler's flow analysis.
            IsRawMaterial = IsTransportType(pr.PrType);
            TransportMode = string.IsNullOrWhiteSpace(existingPo.TransportMode) ? TransportModes.Order : existingPo.TransportMode;
            TransportContractNumber = existingPo.TransportContractNumber;
            TransporterName = existingPo.TransporterName;
            TransportRatePerUnit = existingPo.TransportRatePerUnit;

            // Resolved once for the whole PO, and once for what every OTHER PO has ordered, so a
            // duplicate item name cannot make two of this PO's lines share one PR line's target.
            var poMap = PrLineMatcher.Map(existingPo.Items, pr.Items);
            var orderedElsewhere = PrLineMatcher.OrderedQuantities(pr.Items, pr.Pos, p => p.Id != existingPo.Id);

            // If PO already has saved PurchaseOrderItems, populate from them
            if (existingPo.Items != null && existingPo.Items.Count > 0)
            {
                foreach (var poItem in existingPo.Items)
                {
                    poMap.TryGetValue(poItem, out var prItem);
                    var prTarget = prItem?.Quantity ?? poItem.Quantity;

                    // Other POs ordered qty (excluding this PO)
                    var otherPoOrdered = prItem != null && orderedElsewhere.TryGetValue(prItem.Id, out var oe) ? oe : 0m;

                    var itemSelection = new PoRfqItemSelection
                    {
                        Id = poItem.Id,
                        PrItemId = poItem.PrItemId ?? prItem?.Id,
                        RfqItemId = poItem.RfqItemId,
                        ItemName = poItem.ItemName,
                        Quantity = poItem.Quantity,
                        PrTargetQuantity = prTarget,
                        OtherPosOrderedQuantity = otherPoOrdered,
                        Unit = poItem.Unit,
                        IsSelected = true,
                        QuotedUnitPrice = poItem.UnitPrice,
                        Discount = poItem.Discount,
                        OnPriceOrSelectionChanged = OnItemSelectionOrPriceChanged
                    };

                    foreach (var t in poItem.Transports)
                    {
                        itemSelection.Transports.Add(new PoItemTransport
                        {
                            Id = t.Id,
                            PoItemId = poItem.Id,
                            Quantity = t.Quantity,
                            ContractNumber = t.ContractNumber,
                            TransporterName = t.TransporterName,
                            RatePerUnit = t.RatePerUnit,
                            SortOrder = t.SortOrder,
                            Currency = Currency,
                        });
                    }
                    itemSelection.IsLineTransport = TransportMode == TransportModes.Line;
                    itemSelection.NotifyTransportChanged();

                    Items.Add(itemSelection);
                }

                // Also add any other PR items not in this PO as unselected so user can add them if needed
                if (pr.Items != null)
                {
                    var covered = new HashSet<Guid>(poMap.Values.Select(p => p.Id));
                    var rfqMap = PrLineMatcher.Map(linkedRfq?.Items, pr.Items);
                    foreach (var prItem in pr.Items)
                    {
                        if (!covered.Contains(prItem.Id))
                        {
                            var rfqItem = rfqMap.FirstOrDefault(kv => kv.Value.Id == prItem.Id).Key;

                            var otherPoOrdered = orderedElsewhere.TryGetValue(prItem.Id, out var oe2) ? oe2 : 0m;

                            var remainingAvail = Math.Max(0m, prItem.Quantity - otherPoOrdered);

                            var unselectedItem = new PoRfqItemSelection
                            {
                                Id = Guid.NewGuid(),
                                PrItemId = prItem.Id,
                                RfqItemId = rfqItem?.Id,
                                ItemName = prItem.ItemName,
                                Quantity = remainingAvail,
                                PrTargetQuantity = prItem.Quantity,
                                OtherPosOrderedQuantity = otherPoOrdered,
                                Unit = prItem.Unit,
                                IsSelected = false,
                                QuotedUnitPrice = rfqItem?.QuotedUnitPrice,
                                Discount = rfqItem?.Discount,
                                LastPrice = rfqItem?.LastPrice,
                                OnPriceOrSelectionChanged = OnItemSelectionOrPriceChanged
                            };
                            Items.Add(unselectedItem);
                        }
                    }
                }

                if (!CustomBaseAmount.HasValue || CustomBaseAmount.Value == 0)
                {
                    CustomBaseAmount = Items.Where(i => i.IsSelected).Sum(i => i.LineTotal);
                }
            }
            else if (linkedRfq?.Items != null && linkedRfq.Items.Count > 0)
            {
                // Fallback to linked RFQ items if existing PO didn't have items saved yet
                var fallbackMap = PrLineMatcher.Map(linkedRfq.Items, pr.Items);
                foreach (var rfqItem in linkedRfq.Items)
                {
                    fallbackMap.TryGetValue(rfqItem, out var prItem);
                    var prTarget = prItem?.Quantity ?? rfqItem.Quantity;

                    var otherPoOrdered = prItem != null && orderedElsewhere.TryGetValue(prItem.Id, out var oe3) ? oe3 : 0m;

                    var itemSelection = new PoRfqItemSelection
                    {
                        Id = Guid.NewGuid(),
                        RfqItemId = rfqItem.Id,
                        PrItemId = rfqItem.PrItemId ?? prItem?.Id,
                        ItemName = rfqItem.ItemName,
                        Quantity = rfqItem.Quantity,
                        PrTargetQuantity = prTarget,
                        OtherPosOrderedQuantity = otherPoOrdered,
                        Unit = rfqItem.Unit,
                        IsSelected = rfqItem.IsQuoted,
                        QuotedUnitPrice = rfqItem.QuotedUnitPrice,
                        Discount = rfqItem.Discount,
                        LastPrice = rfqItem.LastPrice,
                        OnPriceOrSelectionChanged = OnItemSelectionOrPriceChanged
                    };
                    Items.Add(itemSelection);
                }
                // existingPo.Value is the VAT-inclusive landed total; using it as the net base
                // re-applied VAT (and freight/charges) on every edit round-trip.
                if (existingPo.BaseAmount is not > 0m)
                {
                    CustomBaseAmount = existingPo.Value > 0 ? DeriveNetBase(existingPo) : Items.Where(i => i.IsSelected).Sum(i => i.LineTotal);
                }
            }
            else if (existingPo.BaseAmount is not > 0m)
            {
                CustomBaseAmount = DeriveNetBase(existingPo);
            }
        }

        // Backs the net base out of a stored VAT-inclusive Value for rows saved before
        // BaseAmount existed (or by paths that stored no breakdown). Uses the selection's own
        // effective charges (which include the linked-RFQ fallbacks applied above) — netting out
        // only the PO's stored charges left the RFQ-sourced copies counted twice.
        private decimal DeriveNetBase(PurchaseOrder po)
        {
            var vat = VatType == "5%" ? 1.05m : 1.0m;
            var net = po.Value / vat;
            return Math.Max(0m, net - (Freight ?? 0m) - (OtherCharges ?? 0m) + (OverallDiscount ?? 0m));
        }

        private bool _bulkSelecting;

        public void OnItemSelectionOrPriceChanged()
        {
            if (_bulkSelecting) return;
            if (HasItems)
            {
                var total = Items.Where(i => i.IsSelected).Sum(i => i.LineTotal);
                if (CustomBaseAmount != total)
                {
                    // The setter's OnCustomBaseAmountChanged hook runs NotifyCalculationsChanged;
                    // calling it again here doubled every recalc.
                    CustomBaseAmount = total;
                    return;
                }
            }
            NotifyCalculationsChanged();
        }

        public void NotifyCalculationsChanged()
        {
            OnPropertyChanged(nameof(BaseAmount));
            OnPropertyChanged(nameof(FreightAmount));
            OnPropertyChanged(nameof(OtherChargesAmount));
            OnPropertyChanged(nameof(OverallDiscountAmount));
            OnPropertyChanged(nameof(NetTaxableAmount));
            OnPropertyChanged(nameof(EffectiveVatAmount));
            OnPropertyChanged(nameof(DisplayTotalAmount));
            OnPropertyChanged(nameof(FormattedDisplayAmount));
            OnPropertyChanged(nameof(FormattedBaseAmount));
            OnPropertyChanged(nameof(FormattedVatAmount));
            OnPropertyChanged(nameof(TransportTotal));
            OnPropertyChanged(nameof(FormattedTransportTotal));
            OnPropertyChanged(nameof(UnallocatedLineCount));
            OnPropertyChanged(nameof(HasUnallocatedLines));
            OnPropertyChanged(nameof(UnallocatedLineText));
            OnPropertyChanged(nameof(SelectedItemsCount));
            OnPropertyChanged(nameof(AllItemsSelected));
            OnPropertyChanged(nameof(PricedItemsCount));
            OnPropertyChanged(nameof(SelectedItemsSummary));
            OnPropertyChanged(nameof(SummaryTitle));
            OnPropertyChanged(nameof(HasOverAllocatedItems));
            OnPropertyChanged(nameof(OrderedQuantity));
            OnPropertyChanged(nameof(TransportTotal));
            OnPropertyChanged(nameof(FormattedTransportTotal));

            OnTotalsRecalculated?.Invoke();
        }

        public void SelectAllItems()
        {
            // Each IsSelected flip invokes OnItemSelectionOrPriceChanged; suppressed during the
            // loop so N items cost one recalc, not N full sum-and-notify passes.
            _bulkSelecting = true;
            foreach (var item in Items)
            {
                item.IsSelected = true;
            }
            _bulkSelecting = false;
            OnItemSelectionOrPriceChanged();
        }

        public void DeselectAllItems()
        {
            _bulkSelecting = true;
            foreach (var item in Items)
            {
                item.IsSelected = false;
            }
            _bulkSelecting = false;
            OnItemSelectionOrPriceChanged();
        }
    }
}
