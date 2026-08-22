using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PoRfqItemSelection : ObservableObject
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

        public decimal TotalAllocated => OtherPosOrderedQuantity + (IsSelected ? Quantity : 0m);

        public decimal MaxAllowedQuantity => Math.Max(0m, PrTargetQuantity - OtherPosOrderedQuantity);

        public bool IsOverAllocated => TotalAllocated > PrTargetQuantity && PrTargetQuantity > 0;

        public bool IsFullyAllocated => TotalAllocated == PrTargetQuantity && PrTargetQuantity > 0;

        public bool IsPendingAllocation => TotalAllocated < PrTargetQuantity && PrTargetQuantity > 0;

        public decimal OverAllocatedQuantity => IsOverAllocated ? (TotalAllocated - PrTargetQuantity) : 0m;

        public decimal PendingQuantity => Math.Max(0m, PrTargetQuantity - TotalAllocated);

        public string AllocationStatusText
        {
            get
            {
                var unitStr = string.IsNullOrWhiteSpace(Unit) ? "pcs" : Unit;
                var totalAllocated = TotalAllocated;
                var thisPoQty = IsSelected ? Quantity : 0m;

                if (IsOverAllocated)
                {
                    return $"Exceeds PR target by {OverAllocatedQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} (PR Target: {PrTargetQuantity:G29} {unitStr})";
                }

                if (IsFullyAllocated)
                {
                    if (thisPoQty > 0 && OtherPosOrderedQuantity > 0)
                        return $"PR Target: {PrTargetQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} (Other POs: {OtherPosOrderedQuantity:G29}, This PO: {thisPoQty:G29}) • Fully Allocated";
                    if (thisPoQty == 0 && OtherPosOrderedQuantity > 0)
                        return $"PR Target: {PrTargetQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} (Other POs: {OtherPosOrderedQuantity:G29}) • Fully Allocated";
                    return $"PR Target: {PrTargetQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} • Fully Allocated";
                }

                var pending = PendingQuantity;
                if (thisPoQty > 0 && OtherPosOrderedQuantity > 0)
                {
                    return $"PR Target: {PrTargetQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} (Other POs: {OtherPosOrderedQuantity:G29}, This PO: {thisPoQty:G29}) • {pending:G29} {unitStr} Pending";
                }

                if (thisPoQty > 0)
                {
                    return $"PR Target: {PrTargetQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} (This PO: {thisPoQty:G29}) • {pending:G29} {unitStr} Pending";
                }

                if (OtherPosOrderedQuantity > 0)
                {
                    return $"PR Target: {PrTargetQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} (Other POs: {OtherPosOrderedQuantity:G29}) • {pending:G29} {unitStr} Pending";
                }

                return $"PR Target: {PrTargetQuantity.ToString("G29", CultureInfo.InvariantCulture)} {unitStr} • {pending:G29} {unitStr} Pending (Unallocated)";
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

    public partial class PoRfqSelection : ObservableObject
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

        partial void OnFreightChanged(decimal? value) => NotifyCalculationsChanged();
        partial void OnOtherChargesChanged(decimal? value) => NotifyCalculationsChanged();
        partial void OnOverallDiscountChanged(decimal? value) => NotifyCalculationsChanged();
        partial void OnVatTypeChanged(string value) => NotifyCalculationsChanged();
        partial void OnCustomBaseAmountChanged(decimal? value) => NotifyCalculationsChanged();

        public bool HasItems => Items != null && Items.Count > 0;
        public int TotalItemsCount => Items?.Count ?? 0;
        public int SelectedItemsCount => Items?.Count(i => i.IsSelected) ?? 0;
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
                var cur = string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency;
                if (!string.IsNullOrWhiteSpace(RfqNumber))
                    return $"{VendorName} • {cur} {DisplayTotalAmount:N0} ({RfqNumber})";
                return $"{VendorName} • {cur} {DisplayTotalAmount:N0}";
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

            if (rfq.Items != null && rfq.Items.Count > 0)
            {
                foreach (var rfqItem in rfq.Items)
                {
                    var prItem = pr?.Items?.FirstOrDefault(pi => (rfqItem.PrItemId.HasValue && pi.Id == rfqItem.PrItemId.Value) || string.Equals(pi.ItemName, rfqItem.ItemName, StringComparison.OrdinalIgnoreCase));
                    var prTarget = prItem?.Quantity ?? rfqItem.Quantity;

                    var otherPoOrdered = pr?.Pos?
                        .SelectMany(p => p.Items ?? Enumerable.Empty<PurchaseOrderItem>())
                        .Where(pi => (prItem != null && pi.PrItemId == prItem.Id) || string.Equals(pi.ItemName, rfqItem.ItemName, StringComparison.OrdinalIgnoreCase))
                        .Sum(pi => pi.Quantity) ?? 0m;

                    var remainingAvail = Math.Max(0m, prTarget - otherPoOrdered);

                    var itemSelection = new PoRfqItemSelection
                    {
                        Id = Guid.NewGuid(),
                        RfqItemId = rfqItem.Id,
                        PrItemId = rfqItem.PrItemId ?? prItem?.Id,
                        ItemName = rfqItem.ItemName,
                        Quantity = (remainingAvail > 0 && remainingAvail < rfqItem.Quantity) ? remainingAvail : rfqItem.Quantity,
                        PrTargetQuantity = prTarget,
                        OtherPosOrderedQuantity = otherPoOrdered,
                        Unit = rfqItem.Unit,
                        IsSelected = rfqItem.IsQuoted && (rfqItem.QuotedUnitPrice.HasValue && rfqItem.QuotedUnitPrice.Value > 0 || rfqItem.LineTotal > 0),
                        QuotedUnitPrice = rfqItem.QuotedUnitPrice,
                        Discount = rfqItem.Discount,
                        LastPrice = rfqItem.LastPrice,
                        OnPriceOrSelectionChanged = OnItemSelectionOrPriceChanged
                    };
                    Items.Add(itemSelection);
                }
            }

            // Also add any other PR items not in RFQ so the user can select and price them if desired
            if (pr?.Items != null)
            {
                foreach (var prItem in pr.Items)
                {
                    if (!Items.Any(i => (prItem.Id != Guid.Empty && i.PrItemId == prItem.Id) || string.Equals(i.ItemName, prItem.ItemName, StringComparison.OrdinalIgnoreCase)))
                    {
                        var otherPoOrdered = pr.Pos?
                            .SelectMany(p => p.Items ?? Enumerable.Empty<PurchaseOrderItem>())
                            .Where(pi => pi.PrItemId == prItem.Id || string.Equals(pi.ItemName, prItem.ItemName, StringComparison.OrdinalIgnoreCase))
                            .Sum(pi => pi.Quantity) ?? 0m;

                        var remainingAvail = Math.Max(0m, prItem.Quantity - otherPoOrdered);

                        var unquotedItem = new PoRfqItemSelection
                        {
                            Id = Guid.NewGuid(),
                            PrItemId = prItem.Id,
                            ItemName = prItem.ItemName,
                            Quantity = remainingAvail > 0 ? remainingAvail : prItem.Quantity,
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

            // If PO already has saved PurchaseOrderItems, populate from them
            if (existingPo.Items != null && existingPo.Items.Count > 0)
            {
                foreach (var poItem in existingPo.Items)
                {
                    var prItem = pr.Items?.FirstOrDefault(pi => (poItem.PrItemId.HasValue && pi.Id == poItem.PrItemId.Value) || string.Equals(pi.ItemName, poItem.ItemName, StringComparison.OrdinalIgnoreCase));
                    var prTarget = prItem?.Quantity ?? poItem.Quantity;

                    // Other POs ordered qty (excluding this PO)
                    var otherPoOrdered = pr.Pos?
                        .Where(p => p.Id != existingPo.Id)
                        .SelectMany(p => p.Items ?? Enumerable.Empty<PurchaseOrderItem>())
                        .Where(pi => (prItem != null && pi.PrItemId == prItem.Id) || string.Equals(pi.ItemName, poItem.ItemName, StringComparison.OrdinalIgnoreCase))
                        .Sum(pi => pi.Quantity) ?? 0m;

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
                    Items.Add(itemSelection);
                }

                // Also add any other PR items not in this PO as unselected so user can add them if needed
                if (pr.Items != null)
                {
                    foreach (var prItem in pr.Items)
                    {
                        if (!Items.Any(i => (prItem.Id != Guid.Empty && i.PrItemId == prItem.Id) || string.Equals(i.ItemName, prItem.ItemName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var rfqItem = linkedRfq?.Items?.FirstOrDefault(ri => (prItem.Id != Guid.Empty && ri.PrItemId == prItem.Id) || string.Equals(ri.ItemName, prItem.ItemName, StringComparison.OrdinalIgnoreCase));

                            var otherPoOrdered = pr.Pos?
                                .Where(p => p.Id != existingPo.Id)
                                .SelectMany(p => p.Items ?? Enumerable.Empty<PurchaseOrderItem>())
                                .Where(pi => pi.PrItemId == prItem.Id || string.Equals(pi.ItemName, prItem.ItemName, StringComparison.OrdinalIgnoreCase))
                                .Sum(pi => pi.Quantity) ?? 0m;

                            var remainingAvail = Math.Max(0m, prItem.Quantity - otherPoOrdered);

                            var unselectedItem = new PoRfqItemSelection
                            {
                                Id = Guid.NewGuid(),
                                PrItemId = prItem.Id,
                                RfqItemId = rfqItem?.Id,
                                ItemName = prItem.ItemName,
                                Quantity = remainingAvail > 0 ? remainingAvail : prItem.Quantity,
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
                foreach (var rfqItem in linkedRfq.Items)
                {
                    var prItem = pr.Items?.FirstOrDefault(pi => (rfqItem.PrItemId.HasValue && pi.Id == rfqItem.PrItemId.Value) || string.Equals(pi.ItemName, rfqItem.ItemName, StringComparison.OrdinalIgnoreCase));
                    var prTarget = prItem?.Quantity ?? rfqItem.Quantity;

                    var otherPoOrdered = pr.Pos?
                        .Where(p => p.Id != existingPo.Id)
                        .SelectMany(p => p.Items ?? Enumerable.Empty<PurchaseOrderItem>())
                        .Where(pi => (prItem != null && pi.PrItemId == prItem.Id) || string.Equals(pi.ItemName, rfqItem.ItemName, StringComparison.OrdinalIgnoreCase))
                        .Sum(pi => pi.Quantity) ?? 0m;

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
                CustomBaseAmount = existingPo.Value > 0 ? existingPo.Value : Items.Where(i => i.IsSelected).Sum(i => i.LineTotal);
            }
            else
            {
                CustomBaseAmount = existingPo.Value;
            }
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
            OnPropertyChanged(nameof(SelectedItemsCount));
            OnPropertyChanged(nameof(PricedItemsCount));
            OnPropertyChanged(nameof(SelectedItemsSummary));
            OnPropertyChanged(nameof(SummaryTitle));
            OnPropertyChanged(nameof(HasOverAllocatedItems));

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
