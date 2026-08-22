using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;
using Procure.Services.Export;
using Procure.Utilities;

namespace Procure.PageModels
{
    // Inline RFQ operations: the add/edit modal, quote totals and pricing paste.
    public partial class PrListPageModel
    {
        // Inline Add / Edit RFQ form state
        [ObservableProperty]
        public partial bool IsAddRfqModalVisible { get; set; }
        [ObservableProperty]
        public partial bool IsEditingRfq { get; set; }
        [ObservableProperty]
        public partial string ModalRfqTitle { get; set; } = "Add Request for Quotation (RFQ)";
        [ObservableProperty]
        public partial RequestForQuotation? EditingRfq { get; set; }
        [ObservableProperty]
        public partial PurchaseRequisition? TargetPrForRfq { get; set; }
        [ObservableProperty]
        public partial string NewRfqNo { get; set; } = string.Empty;
        [ObservableProperty]
        public partial string NewRfqVendor { get; set; } = string.Empty;
        [ObservableProperty]
        public partial string NewRfqCurrency { get; set; } = "AED";
        [ObservableProperty]
        public partial decimal? NewRfqQuoteAmount { get; set; }
        [ObservableProperty]
        public partial decimal? NewRfqFreight { get; set; }
        [ObservableProperty]
        public partial decimal? NewRfqOtherCharges { get; set; }
        [ObservableProperty]
        public partial decimal? NewRfqDiscount { get; set; }
        [ObservableProperty]
        public partial string NewRfqPaymentTerms { get; set; } = "30 Days Net";
        [ObservableProperty]
        public partial string NewRfqVatType { get; set; } = "5%";
        [ObservableProperty]
        public partial string NewRfqIncoterms { get; set; } = "DDP";
        [ObservableProperty]
        public partial string NewRfqDeliveryLeadTime { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewRfqWarranty { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewRfqTechnicalApproval { get; set; } = string.Empty;

        public List<string> AvailableTechnicalApprovals { get; } = new() { "Approved", "Not Approved" };

        [ObservableProperty]
        public partial ObservableCollection<RfqItem> EditingRfqItems { get; set; } = new();

        [ObservableProperty]
        public partial bool HasEditingRfqItems { get; set; }

        [ObservableProperty]
        public partial decimal CalculatedRfqBaseTotal { get; set; }

        [ObservableProperty]
        public partial decimal CalculatedRfqVatAmount { get; set; }

        [ObservableProperty]
        public partial decimal CalculatedRfqGrandTotal { get; set; }

        [ObservableProperty]
        public partial string FormattedCalculatedRfqGrandTotal { get; set; } = string.Empty;

        partial void OnNewRfqQuoteAmountChanged(decimal? value) => RecalculateRfqTotals();
        partial void OnNewRfqFreightChanged(decimal? value) => RecalculateRfqTotals();
        partial void OnNewRfqOtherChargesChanged(decimal? value) => RecalculateRfqTotals();
        partial void OnNewRfqDiscountChanged(decimal? value) => RecalculateRfqTotals();
        partial void OnNewRfqVatTypeChanged(string value) => RecalculateRfqTotals();
        partial void OnNewRfqCurrencyChanged(string value) => RecalculateRfqTotals();

        public IReadOnlyList<string> AvailableCurrencies => AppConstants.SupportedCurrencies;
        public List<string> AvailableVatTypes { get; } = new() { "5%", "RC", "V0" };
        public List<string> AvailableIncoterms { get; } = new() { "DDP", "DAP", "CIF", "FOB", "EXW", "CFR", "FCA", "CIP", "CPT" };


        // ================= RFQ INLINE OPERATIONS =================

        public void RecalculateRfqTotals()
        {
            if (EditingRfqItems != null && EditingRfqItems.Count > 0)
            {
                var quotedSum = EditingRfqItems.Where(i => i.IsQuoted).Sum(i => i.LineTotal);
                if (quotedSum > 0)
                {
                    CalculatedRfqBaseTotal = quotedSum;
                    NewRfqQuoteAmount = quotedSum;
                }
                else
                {
                    CalculatedRfqBaseTotal = NewRfqQuoteAmount ?? 0m;
                }
            }
            else
            {
                CalculatedRfqBaseTotal = NewRfqQuoteAmount ?? 0m;
            }

            var freight = NewRfqFreight ?? 0m;
            var otherCharges = NewRfqOtherCharges ?? 0m;
            var discount = NewRfqDiscount ?? 0m;
            var netTaxable = Math.Max(0m, (CalculatedRfqBaseTotal + freight + otherCharges) - discount);
            var vatType = string.IsNullOrWhiteSpace(NewRfqVatType) ? "5%" : NewRfqVatType;
            CalculatedRfqVatAmount = (vatType == "5%") ? netTaxable * 0.05m : 0m;
            CalculatedRfqGrandTotal = netTaxable + CalculatedRfqVatAmount;

            var cur = string.IsNullOrWhiteSpace(NewRfqCurrency) ? "AED" : NewRfqCurrency;
            FormattedCalculatedRfqGrandTotal = $"{cur} {CalculatedRfqGrandTotal:N2}";
            HasEditingRfqItems = EditingRfqItems != null && EditingRfqItems.Count > 0;
        }

        private void OnEditingRfqItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RfqItem.IsQuoted) ||
                e.PropertyName == nameof(RfqItem.QuotedUnitPrice) ||
                e.PropertyName == nameof(RfqItem.Discount) ||
                e.PropertyName == nameof(RfqItem.Quantity) ||
                e.PropertyName == nameof(RfqItem.LineTotal))
            {
                RecalculateRfqTotals();
            }
        }

        [RelayCommand]
        public void SelectAllRfqItems()
        {
            foreach (var item in EditingRfqItems)
            {
                item.IsQuoted = true;
            }
            RecalculateRfqTotals();
        }

        [RelayCommand]
        public void DeselectAllRfqItems()
        {
            foreach (var item in EditingRfqItems)
            {
                item.IsQuoted = false;
            }
            RecalculateRfqTotals();
        }

        [RelayCommand]
        public async Task CopySelectedRfqItemsForEmailAsync()
        {
            if (EditingRfqItems == null || EditingRfqItems.Count == 0)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("No Items", "There are no items in this RFQ to copy.", "OK");
                return;
            }

            var selectedItems = EditingRfqItems.Where(i => i.IsQuoted && !string.IsNullOrWhiteSpace(i.ItemName)).ToList();
            if (selectedItems.Count == 0)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("No Selected Items", "Please select at least one item to copy for email.", "OK");
                return;
            }

            try
            {
                await RfqClipboardFormatter.CopyToClipboardAsync(selectedItems);
                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Copied to Clipboard",
                        $"Successfully copied {selectedItems.Count} item(s) formatted for your email.\n\nYou can now paste directly into Outlook, Gmail, or Word.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public void HandleRfqPricingPaste(RfqItem startItem, string rawText, RfqPricingColumn targetColumn)
        {
            if (string.IsNullOrWhiteSpace(rawText) || EditingRfqItems == null || EditingRfqItems.Count == 0) return;

            var rows = ClipboardItemParser.ParseRfqPricingData(rawText, targetColumn);
            if (rows.Count == 0) return;

            var startIndex = EditingRfqItems.IndexOf(startItem);
            if (startIndex < 0) startIndex = 0;

            for (int i = 0; i < rows.Count && (startIndex + i) < EditingRfqItems.Count; i++)
            {
                var targetItem = EditingRfqItems[startIndex + i];
                var row = rows[i];

                if (row.HasUnitPrice)
                {
                    targetItem.QuotedUnitPrice = row.UnitPrice;
                    if (row.UnitPrice.HasValue)
                    {
                        targetItem.IsQuoted = true;
                    }
                }

                if (row.HasDiscount)
                {
                    targetItem.Discount = row.Discount;
                }

                if (row.HasLastPrice)
                {
                    targetItem.LastPrice = row.LastPrice;
                }
            }

            RecalculateRfqTotals();
        }

        public void HandleRfqUnitPricePaste(RfqItem startItem, string rawText) =>
            HandleRfqPricingPaste(startItem, rawText, RfqPricingColumn.UnitPrice);

        public void HandleRfqDiscountPaste(RfqItem startItem, string rawText) =>
            HandleRfqPricingPaste(startItem, rawText, RfqPricingColumn.Discount);

        public void HandleRfqLastPricePaste(RfqItem startItem, string rawText) =>
            HandleRfqPricingPaste(startItem, rawText, RfqPricingColumn.LastPrice);

        [RelayCommand]
        public void OpenAddRfqModal(PurchaseRequisition pr)
        {
            EditingRfq = null;
            IsEditingRfq = false;
            ModalRfqTitle = "Add Request for Quotation (RFQ)";
            TargetPrForRfq = pr;
            NewRfqNo = string.Empty;
            NewRfqVendor = string.Empty;
            NewRfqCurrency = string.IsNullOrWhiteSpace(_settingsService.DefaultCurrency) ? "AED" : _settingsService.DefaultCurrency;
            NewRfqQuoteAmount = null;
            NewRfqFreight = null;
            NewRfqOtherCharges = null;
            NewRfqDiscount = null;
            NewRfqPaymentTerms = "30 Days Net";
            NewRfqVatType = "5%";
            NewRfqIncoterms = "DDP";
            NewRfqDeliveryLeadTime = string.Empty;
            NewRfqWarranty = string.Empty;
            NewRfqTechnicalApproval = string.Empty;

            foreach (var item in EditingRfqItems)
                item.PropertyChanged -= OnEditingRfqItemPropertyChanged;

            EditingRfqItems.Clear();

            // Automatically clone all line items from the PR into the RFQ
            if (pr.Items != null && pr.Items.Count > 0)
            {
                int sortOrder = 0;
                foreach (var prItem in pr.Items)
                {
                    var rfqItem = new RfqItem
                    {
                        Id = Guid.NewGuid(),
                        PrItemId = prItem.Id,
                        ItemName = prItem.ItemName,
                        Quantity = prItem.Quantity,
                        Unit = prItem.Unit,
                        IsQuoted = true,
                        QuotedUnitPrice = null,
                        Discount = null,
                        LastPrice = prItem.EstimatedUnitPrice,
                        SortOrder = sortOrder++
                    };
                    rfqItem.PropertyChanged += OnEditingRfqItemPropertyChanged;
                    EditingRfqItems.Add(rfqItem);
                }
            }

            RecalculateRfqTotals();
            IsAddRfqModalVisible = true;
        }

        [RelayCommand]
        public void OpenEditRfqModal(RequestForQuotation rfq)
        {
            if (rfq == null) return;
            EditingRfq = rfq;
            IsEditingRfq = true;
            ModalRfqTitle = $"Edit Commercial Terms - {rfq.Vendor}";
            TargetPrForRfq = LoadedPrs.FirstOrDefault(p => p.Id == rfq.PrId);
            NewRfqNo = rfq.RfqNo;
            NewRfqVendor = rfq.Vendor;
            NewRfqCurrency = string.IsNullOrWhiteSpace(rfq.Currency) ? _settingsService.DefaultCurrency : rfq.Currency;
            NewRfqQuoteAmount = rfq.QuoteAmount;
            NewRfqFreight = rfq.Freight;
            NewRfqOtherCharges = rfq.OtherCharges;
            NewRfqDiscount = rfq.Discount;
            NewRfqPaymentTerms = string.IsNullOrWhiteSpace(rfq.PaymentTerms) ? "30 Days Net" : rfq.PaymentTerms;
            NewRfqVatType = string.IsNullOrWhiteSpace(rfq.VatType) ? "5%" : rfq.VatType;
            NewRfqIncoterms = string.IsNullOrWhiteSpace(rfq.Incoterms) ? "DDP" : rfq.Incoterms;
            NewRfqDeliveryLeadTime = rfq.DeliveryLeadTime ?? string.Empty;
            NewRfqWarranty = rfq.Warranty ?? string.Empty;
            NewRfqTechnicalApproval = rfq.TechnicalApproval ?? string.Empty;

            foreach (var item in EditingRfqItems)
                item.PropertyChanged -= OnEditingRfqItemPropertyChanged;

            EditingRfqItems.Clear();

            if (rfq.Items != null && rfq.Items.Count > 0)
            {
                foreach (var rfqItem in rfq.Items)
                {
                    var clone = new RfqItem
                    {
                        Id = rfqItem.Id,
                        RfqId = rfq.Id,
                        PrItemId = rfqItem.PrItemId,
                        ItemName = rfqItem.ItemName,
                        Quantity = rfqItem.Quantity,
                        Unit = rfqItem.Unit,
                        IsQuoted = rfqItem.IsQuoted,
                        QuotedUnitPrice = rfqItem.QuotedUnitPrice,
                        Discount = rfqItem.Discount,
                        LastPrice = rfqItem.LastPrice,
                        Notes = rfqItem.Notes,
                        SortOrder = rfqItem.SortOrder
                    };
                    clone.PropertyChanged += OnEditingRfqItemPropertyChanged;
                    EditingRfqItems.Add(clone);
                }
            }
            else if (TargetPrForRfq?.Items != null && TargetPrForRfq.Items.Count > 0)
            {
                // Populate from PR items for legacy RFQs that didn't have RfqItem rows
                int sortOrder = 0;
                foreach (var prItem in TargetPrForRfq.Items)
                {
                    var rfqItem = new RfqItem
                    {
                        Id = Guid.NewGuid(),
                        RfqId = rfq.Id,
                        PrItemId = prItem.Id,
                        ItemName = prItem.ItemName,
                        Quantity = prItem.Quantity,
                        Unit = prItem.Unit,
                        IsQuoted = true,
                        QuotedUnitPrice = null,
                        Discount = null,
                        LastPrice = prItem.EstimatedUnitPrice,
                        SortOrder = sortOrder++
                    };
                    rfqItem.PropertyChanged += OnEditingRfqItemPropertyChanged;
                    EditingRfqItems.Add(rfqItem);
                }
            }

            RecalculateRfqTotals();
            IsAddRfqModalVisible = true;
        }

        [RelayCommand]
        public async Task SaveNewRfqAsync()
        {
            if (TargetPrForRfq == null && EditingRfq != null)
            {
                TargetPrForRfq = LoadedPrs.FirstOrDefault(p => p.Id == EditingRfq.PrId);
            }

            if (TargetPrForRfq == null) return;

            if (string.IsNullOrWhiteSpace(NewRfqNo))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Please enter an RFQ Number.", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(NewRfqVendor))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Please enter a Vendor name.", "OK");
                return;
            }

            try
            {
                if (IsEditingRfq && EditingRfq != null)
                {
                    // Update existing RFQ
                    EditingRfq.RfqNo = string.IsNullOrWhiteSpace(NewRfqNo) ? EditingRfq.RfqNo : NewRfqNo.Trim();
                    EditingRfq.Vendor = NewRfqVendor.Trim();
                    EditingRfq.Currency = string.IsNullOrWhiteSpace(NewRfqCurrency) ? "AED" : NewRfqCurrency.Trim();
                    EditingRfq.QuoteAmount = CalculatedRfqBaseTotal > 0 ? CalculatedRfqBaseTotal : NewRfqQuoteAmount;
                    EditingRfq.Freight = NewRfqFreight;
                    EditingRfq.OtherCharges = NewRfqOtherCharges;
                    EditingRfq.Discount = NewRfqDiscount;
                    EditingRfq.PaymentTerms = string.IsNullOrWhiteSpace(NewRfqPaymentTerms) ? "30 Days Net" : NewRfqPaymentTerms.Trim();
                    EditingRfq.VatType = string.IsNullOrWhiteSpace(NewRfqVatType) ? "5%" : NewRfqVatType;
                    EditingRfq.Incoterms = string.IsNullOrWhiteSpace(NewRfqIncoterms) ? "DDP" : NewRfqIncoterms;
                    EditingRfq.DeliveryLeadTime = NewRfqDeliveryLeadTime?.Trim() ?? string.Empty;
                    EditingRfq.Warranty = NewRfqWarranty?.Trim() ?? string.Empty;
                    EditingRfq.TechnicalApproval = NewRfqTechnicalApproval?.Trim() ?? string.Empty;

                    // Update items
                    EditingRfq.Items = new ObservableCollection<RfqItem>(EditingRfqItems);

                    if ((EditingRfq.QuoteAmount.HasValue && EditingRfq.QuoteAmount.Value > 0) || (EditingRfq.HasLineItems && EditingRfq.QuotedItemsCount > 0 && EditingRfq.BaseAmount > 0))
                    {
                        if (EditingRfq.Status == RfqStatus.Sent)
                        {
                            EditingRfq.Status = RfqStatus.QuoteReceived;
                            EditingRfq.QuoteReceivedDate = DateTime.Today;
                        }
                    }

                    await _prRepo.SaveRfqAsync(EditingRfq);
                    EditingRfq.NotifyCalculationsChanged();
                    TargetPrForRfq.NotifyHierarchyChanged();

                    foreach (var item in EditingRfqItems)
                        item.PropertyChanged -= OnEditingRfqItemPropertyChanged;

                    EditingRfqItems.Clear();
                    IsAddRfqModalVisible = false;
                    return;
                }

                // Create new RFQ
                var rfq = new RequestForQuotation
                {
                    Id = Guid.NewGuid(),
                    PrId = TargetPrForRfq.Id,
                    RfqNo = string.IsNullOrWhiteSpace(NewRfqNo) ? $"RFQ-{TargetPrForRfq.Rfqs.Count + 1}" : NewRfqNo,
                    Vendor = NewRfqVendor.Trim(),
                    Currency = string.IsNullOrWhiteSpace(NewRfqCurrency) ? "AED" : NewRfqCurrency.Trim(),
                    QuoteAmount = CalculatedRfqBaseTotal > 0 ? CalculatedRfqBaseTotal : NewRfqQuoteAmount,
                    PaymentTerms = string.IsNullOrWhiteSpace(NewRfqPaymentTerms) ? "30 Days Net" : NewRfqPaymentTerms.Trim(),
                    VatType = string.IsNullOrWhiteSpace(NewRfqVatType) ? "5%" : NewRfqVatType,
                    Freight = NewRfqFreight,
                    OtherCharges = NewRfqOtherCharges,
                    Discount = NewRfqDiscount,
                    Incoterms = string.IsNullOrWhiteSpace(NewRfqIncoterms) ? "DDP" : NewRfqIncoterms,
                    DeliveryLeadTime = NewRfqDeliveryLeadTime?.Trim() ?? string.Empty,
                    Warranty = NewRfqWarranty?.Trim() ?? string.Empty,
                    TechnicalApproval = NewRfqTechnicalApproval?.Trim() ?? string.Empty,
                    SentDate = DateTime.Today,
                    Items = new ObservableCollection<RfqItem>(EditingRfqItems)
                };

                foreach (var it in rfq.Items)
                    it.RfqId = rfq.Id;

                if ((rfq.QuoteAmount.HasValue && rfq.QuoteAmount.Value > 0) || (rfq.HasLineItems && rfq.QuotedItemsCount > 0 && rfq.BaseAmount > 0))
                {
                    rfq.Status = RfqStatus.QuoteReceived;
                    rfq.QuoteReceivedDate = DateTime.Today;
                }
                else
                {
                    rfq.Status = RfqStatus.Sent;
                }

                await _prRepo.SaveRfqAsync(rfq);
                TargetPrForRfq.Rfqs.Add(rfq);

                // Auto-advance PR status if it was PR Raised
                if (TargetPrForRfq.Status == ProcurementStatus.PrRaised)
                {
                    TargetPrForRfq.Status = ProcurementStatus.RfqSent;
                    await _prRepo.SavePrFieldsAsync(TargetPrForRfq);
                }

                rfq.NotifyCalculationsChanged();
                TargetPrForRfq.NotifyHierarchyChanged();

                foreach (var item in EditingRfqItems)
                    item.PropertyChanged -= OnEditingRfqItemPropertyChanged;

                EditingRfqItems.Clear();
                IsAddRfqModalVisible = false;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void CloseAddRfqModal()
        {
            foreach (var item in EditingRfqItems)
                item.PropertyChanged -= OnEditingRfqItemPropertyChanged;

            EditingRfqItems.Clear();
            IsAddRfqModalVisible = false;
            EditingRfq = null;
            IsEditingRfq = false;
        }

        [RelayCommand]
        public async Task MarkQuoteReceivedAsync(RequestForQuotation rfq)
        {
            if (Shell.Current == null) return;

            var amountStr = await Shell.Current.DisplayPromptAsync(
                "Quote Received",
                $"Enter quote amount for {rfq.Vendor}:",
                "Save",
                "Cancel",
                "Quote Amount (e.g. 5000)",
                keyboard: Keyboard.Numeric);

            if (amountStr == null) return;

            if (decimal.TryParse(amountStr, out var amount))
            {
                rfq.QuoteAmount = amount;
                rfq.QuoteReceivedDate = DateTime.Today;
                rfq.Status = RfqStatus.QuoteReceived;

                await _prRepo.SaveRfqAsync(rfq);

                // Update parent PR status if all quotes received
                var parentPr = _loadedPrs.FirstOrDefault(p => p.Id == rfq.PrId);
                if (parentPr != null)
                {
                    if (parentPr.Rfqs.All(r => r.Status == RfqStatus.QuoteReceived))
                    {
                        parentPr.Status = ProcurementStatus.QuotesReceived;
                        await _prRepo.SavePrFieldsAsync(parentPr);
                    }
                    parentPr.NotifyHierarchyChanged();
                }
            }
        }

        [RelayCommand]
        public async Task DeleteRfqAsync(RequestForQuotation rfq)
        {
            if (Shell.Current == null) return;

            var confirm = await Shell.Current.DisplayAlertAsync("Delete RFQ", $"Delete RFQ for {rfq.Vendor}?", "Delete", "Cancel");
            if (!confirm) return;

            try
            {
                await _prRepo.DeleteRfqAsync(rfq.Id);
                var parentPr = _loadedPrs.FirstOrDefault(p => p.Id == rfq.PrId);
                if (parentPr != null)
                {
                    parentPr.Rfqs.Remove(rfq);
                    parentPr.NotifyHierarchyChanged();
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

    }
}
