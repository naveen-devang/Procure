using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Maui.Alerts;
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
    // Batch operations over the current selection: one shared RFQ, one combined PO.
    public partial class PrListPageModel
    {
        // Batch Shared RFQ modal state
        [ObservableProperty]
        public partial bool IsBatchRfqModalVisible { get; set; }

        [ObservableProperty]
        public partial string BatchRfqNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BatchRfqVendor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BatchRfqCurrency { get; set; } = "AED";

        [ObservableProperty]
        public partial decimal? BatchRfqQuoteAmount { get; set; }

        [ObservableProperty]
        public partial decimal? BatchRfqFreight { get; set; }

        [ObservableProperty]
        public partial decimal? BatchRfqOtherCharges { get; set; }

        [ObservableProperty]
        public partial decimal? BatchRfqDiscount { get; set; }

        [ObservableProperty]
        public partial string BatchRfqPaymentTerms { get; set; } = "30 Days Net";

        [ObservableProperty]
        public partial string BatchRfqVatType { get; set; } = "5%";

        [ObservableProperty]
        public partial string BatchRfqIncoterms { get; set; } = "DDP";

        [ObservableProperty]
        public partial string BatchRfqDeliveryLeadTime { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BatchRfqWarranty { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BatchRfqTechnicalApproval { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BatchRfqPrsSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial ObservableCollection<RfqItem> BatchEditingRfqItems { get; set; } = new();

        [ObservableProperty]
        public partial bool HasBatchEditingRfqItems { get; set; }

        [ObservableProperty]
        public partial decimal CalculatedBatchRfqBaseTotal { get; set; }

        [ObservableProperty]
        public partial decimal CalculatedBatchRfqVatAmount { get; set; }

        [ObservableProperty]
        public partial decimal CalculatedBatchRfqGrandTotal { get; set; }

        [ObservableProperty]
        public partial string FormattedCalculatedBatchRfqGrandTotal { get; set; } = string.Empty;

        partial void OnBatchRfqQuoteAmountChanged(decimal? value) => RecalculateBatchRfqTotals();
        partial void OnBatchRfqFreightChanged(decimal? value) => RecalculateBatchRfqTotals();
        partial void OnBatchRfqOtherChargesChanged(decimal? value) => RecalculateBatchRfqTotals();
        partial void OnBatchRfqDiscountChanged(decimal? value) => RecalculateBatchRfqTotals();
        partial void OnBatchRfqVatTypeChanged(string value) => RecalculateBatchRfqTotals();
        partial void OnBatchRfqCurrencyChanged(string value) => RecalculateBatchRfqTotals();

        // Batch Combined PO modal state
        [ObservableProperty]
        public partial bool IsBatchPoModalVisible { get; set; }

        [ObservableProperty]
        public partial string BatchPoNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BatchPoVendor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial decimal BatchPoTotalValue { get; set; }

        [ObservableProperty]
        public partial string BatchPoStatus { get; set; } = PoStatus.Raised;


        // ================= BATCH SHARED RFQ OPERATIONS =================

        public void RecalculateBatchRfqTotals()
        {
            if (BatchEditingRfqItems != null && BatchEditingRfqItems.Count > 0)
            {
                var quotedSum = BatchEditingRfqItems.Where(i => i.IsQuoted).Sum(i => i.LineTotal);
                if (quotedSum > 0)
                {
                    CalculatedBatchRfqBaseTotal = quotedSum;
                    BatchRfqQuoteAmount = quotedSum;
                }
                else
                {
                    CalculatedBatchRfqBaseTotal = BatchRfqQuoteAmount ?? 0m;
                }
            }
            else
            {
                CalculatedBatchRfqBaseTotal = BatchRfqQuoteAmount ?? 0m;
            }

            var freight = BatchRfqFreight ?? 0m;
            var otherCharges = BatchRfqOtherCharges ?? 0m;
            var discount = BatchRfqDiscount ?? 0m;
            var netTaxable = Math.Max(0m, (CalculatedBatchRfqBaseTotal + freight + otherCharges) - discount);
            var vatType = string.IsNullOrWhiteSpace(BatchRfqVatType) ? "5%" : BatchRfqVatType;
            CalculatedBatchRfqVatAmount = (vatType == "5%") ? netTaxable * 0.05m : 0m;
            CalculatedBatchRfqGrandTotal = netTaxable + CalculatedBatchRfqVatAmount;

            var cur = string.IsNullOrWhiteSpace(BatchRfqCurrency) ? "AED" : BatchRfqCurrency;
            FormattedCalculatedBatchRfqGrandTotal = $"{cur} {CalculatedBatchRfqGrandTotal:N2}";
            HasBatchEditingRfqItems = BatchEditingRfqItems != null && BatchEditingRfqItems.Count > 0;
        }

        private void OnBatchEditingRfqItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RfqItem.IsQuoted) ||
                e.PropertyName == nameof(RfqItem.QuotedUnitPrice) ||
                e.PropertyName == nameof(RfqItem.Discount) ||
                e.PropertyName == nameof(RfqItem.Quantity) ||
                e.PropertyName == nameof(RfqItem.LineTotal))
            {
                RecalculateBatchRfqTotals();
            }
        }

        [RelayCommand]
        public void SelectAllBatchRfqItems()
        {
            foreach (var item in BatchEditingRfqItems)
            {
                item.IsQuoted = true;
            }
            RecalculateBatchRfqTotals();
        }

        [RelayCommand]
        public void DeselectAllBatchRfqItems()
        {
            foreach (var item in BatchEditingRfqItems)
            {
                item.IsQuoted = false;
            }
            RecalculateBatchRfqTotals();
        }

        [RelayCommand]
        public async Task CopySelectedBatchRfqItemsForEmailAsync()
        {
            if (BatchEditingRfqItems == null || BatchEditingRfqItems.Count == 0)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("No Items", "There are no items in this shared RFQ to copy.", "OK");
                return;
            }

            var selectedItems = BatchEditingRfqItems.Where(i => i.IsQuoted && !string.IsNullOrWhiteSpace(i.ItemName)).ToList();
            if (selectedItems.Count == 0)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("No Selected Items", "Please select at least one item to copy for email.", "OK");
                return;
            }

            try
            {
                await RfqClipboardFormatter.CopyToClipboardAsync(selectedItems);
                await Toast.Make($"Copied {selectedItems.Count} item(s) for email").Show();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public void HandleBatchRfqPricingPaste(RfqItem startItem, string rawText, RfqPricingColumn targetColumn)
        {
            if (string.IsNullOrWhiteSpace(rawText) || BatchEditingRfqItems == null || BatchEditingRfqItems.Count == 0) return;

            var rows = ClipboardItemParser.ParseRfqPricingData(rawText, targetColumn);
            if (rows.Count == 0) return;

            var startIndex = BatchEditingRfqItems.IndexOf(startItem);
            if (startIndex < 0) startIndex = 0;

            for (int i = 0; i < rows.Count && (startIndex + i) < BatchEditingRfqItems.Count; i++)
            {
                var targetItem = BatchEditingRfqItems[startIndex + i];
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

            RecalculateBatchRfqTotals();
        }

        public void HandleBatchRfqUnitPricePaste(RfqItem startItem, string rawText) =>
            HandleBatchRfqPricingPaste(startItem, rawText, RfqPricingColumn.UnitPrice);

        public void HandleBatchRfqDiscountPaste(RfqItem startItem, string rawText) =>
            HandleBatchRfqPricingPaste(startItem, rawText, RfqPricingColumn.Discount);

        public void HandleBatchRfqLastPricePaste(RfqItem startItem, string rawText) =>
            HandleBatchRfqPricingPaste(startItem, rawText, RfqPricingColumn.LastPrice);

        [RelayCommand]
        public async Task OpenBatchRfqModalAsync()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count < 1)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Shared RFQ", "Please select at least 1 requisition.", "OK");
                return;
            }

            BatchRfqPrsSummary = $"Shared across {selected.Count} PR(s): {string.Join(", ", selected.Select(p => p.PrNo))}";

            var cleanNumbers = selected.Select(p =>
            {
                var no = p.PrNo.Trim();
                if (no.StartsWith("PR-", StringComparison.OrdinalIgnoreCase))
                    return no.Substring(3).Trim();
                if (no.StartsWith("PR ", StringComparison.OrdinalIgnoreCase))
                    return no.Substring(3).Trim();
                return no;
            }).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            BatchRfqNo = cleanNumbers.Count > 0 ? $"RFQ-{string.Join("/", cleanNumbers)}" : $"RFQ-BUNDLE-{DateTime.Now:MMdd}-{new Random().Next(100, 999)}";
            BatchRfqVendor = string.Empty;
            BatchRfqCurrency = string.IsNullOrWhiteSpace(_settingsService.DefaultCurrency) ? "AED" : _settingsService.DefaultCurrency;
            BatchRfqQuoteAmount = null;
            BatchRfqFreight = null;
            BatchRfqOtherCharges = null;
            BatchRfqDiscount = null;
            BatchRfqPaymentTerms = "30 Days Net";
            BatchRfqVatType = "5%";
            BatchRfqIncoterms = "DDP";
            BatchRfqDeliveryLeadTime = string.Empty;
            BatchRfqWarranty = string.Empty;
            BatchRfqTechnicalApproval = string.Empty;

            foreach (var item in BatchEditingRfqItems)
                item.PropertyChanged -= OnBatchEditingRfqItemPropertyChanged;

            BatchEditingRfqItems.Clear();

            // Consolidate line items from all selected PRs
            int sort = 0;
            foreach (var pr in selected)
            {
                if (pr.Items != null && pr.Items.Count > 0)
                {
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
                            Notes = pr.PrNo, // stores PR number for row badge and PR-item binding
                            SortOrder = sort++
                        };
                        rfqItem.PropertyChanged += OnBatchEditingRfqItemPropertyChanged;
                        BatchEditingRfqItems.Add(rfqItem);
                    }
                }
            }

            RecalculateBatchRfqTotals();
            IsBatchRfqModalVisible = true;
        }

        [RelayCommand]
        public void CloseBatchRfqModal()
        {
            foreach (var item in BatchEditingRfqItems)
                item.PropertyChanged -= OnBatchEditingRfqItemPropertyChanged;

            BatchEditingRfqItems.Clear();
            IsBatchRfqModalVisible = false;
        }

        [RelayCommand]
        public async Task SaveBatchRfqModalAsync()
        {
            if (string.IsNullOrWhiteSpace(BatchRfqNo))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "RFQ Number is required.", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(BatchRfqVendor))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Vendor name is required.", "OK");
                return;
            }

            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;

            try
            {
                IsBusy = true;
                var rfqTemplate = new RequestForQuotation
                {
                    RfqNo = BatchRfqNo.Trim(),
                    Vendor = BatchRfqVendor.Trim(),
                    Currency = string.IsNullOrWhiteSpace(BatchRfqCurrency) ? "AED" : BatchRfqCurrency.Trim(),
                    QuoteAmount = CalculatedBatchRfqBaseTotal > 0 ? CalculatedBatchRfqBaseTotal : BatchRfqQuoteAmount,
                    PaymentTerms = string.IsNullOrWhiteSpace(BatchRfqPaymentTerms) ? "30 Days Net" : BatchRfqPaymentTerms.Trim(),
                    VatType = string.IsNullOrWhiteSpace(BatchRfqVatType) ? "5%" : BatchRfqVatType,
                    Freight = BatchRfqFreight,
                    OtherCharges = BatchRfqOtherCharges,
                    Discount = BatchRfqDiscount,
                    Incoterms = string.IsNullOrWhiteSpace(BatchRfqIncoterms) ? "DDP" : BatchRfqIncoterms,
                    DeliveryLeadTime = BatchRfqDeliveryLeadTime?.Trim() ?? string.Empty,
                    Warranty = BatchRfqWarranty?.Trim() ?? string.Empty,
                    TechnicalApproval = BatchRfqTechnicalApproval?.Trim() ?? string.Empty,
                    SentDate = DateTime.Today
                };

                if ((rfqTemplate.QuoteAmount.HasValue && rfqTemplate.QuoteAmount.Value > 0) || (BatchEditingRfqItems.Any(i => i.IsQuoted && i.LineTotal > 0)))
                {
                    rfqTemplate.Status = RfqStatus.QuoteReceived;
                    rfqTemplate.QuoteReceivedDate = DateTime.Today;
                }
                else
                {
                    rfqTemplate.Status = RfqStatus.Sent;
                }

                await _prRepo.CreateBatchRfqAsync(selected, rfqTemplate, BatchEditingRfqItems);

                foreach (var pr in selected)
                {
                    pr.IsSelected = false;
                    _selectedIds.Remove(pr.Id);
                    pr.NotifyHierarchyChanged();
                }

                foreach (var item in BatchEditingRfqItems)
                    item.PropertyChanged -= OnBatchEditingRfqItemPropertyChanged;

                BatchEditingRfqItems.Clear();
                IsBatchRfqModalVisible = false;
                UpdateSelectionState();
                ApplyFilters();
                UpdateStatusBanner();

                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Shared RFQ Created",
                        $"Shared RFQ {rfqTemplate.RfqNo} created across {selected.Count} requisitions with full item quotes and commercial terms.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ================= BATCH COMBINED PO OPERATIONS =================

        [RelayCommand]
        public async Task OpenBatchPoModalAsync()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count < 1)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Combined PO", "Please select at least 1 requisition.", "OK");
                return;
            }

            BatchPoNo = string.Empty;

            // Look for common vendor in quotes
            var vendors = selected.SelectMany(p => p.Rfqs).Select(r => r.Vendor).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            var mostCommonVendor = vendors.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? string.Empty;
            BatchPoVendor = mostCommonVendor;

            var sumQuotes = selected.SelectMany(p => p.Rfqs).Where(r => string.IsNullOrEmpty(mostCommonVendor) || r.Vendor == mostCommonVendor).Sum(r => r.QuoteAmount ?? 0m);
            BatchPoTotalValue = sumQuotes;
            BatchPoStatus = PoStatus.Raised;

            IsBatchPoModalVisible = true;
        }

        [RelayCommand]
        public void CloseBatchPoModal()
        {
            IsBatchPoModalVisible = false;
        }

        [RelayCommand]
        public async Task SaveBatchPoModalAsync()
        {
            if (string.IsNullOrWhiteSpace(BatchPoNo))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "PO Number is required.", "OK");
                return;
            }

            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;

            try
            {
                IsBusy = true;
                var poTemplate = new PurchaseOrder
                {
                    PoNo = BatchPoNo.Trim(),
                    Vendor = BatchPoVendor.Trim(),
                    Value = BatchPoTotalValue,
                    Status = BatchPoStatus,
                    Date = DateTime.Today
                };

                await _prRepo.CreateBatchPoAsync(selected, poTemplate);

                foreach (var pr in selected)
                {
                    pr.IsSelected = false;
                    _selectedIds.Remove(pr.Id);
                    pr.NotifyHierarchyChanged();
                }

                IsBatchPoModalVisible = false;
                UpdateSelectionState();
                ApplyFilters();
                UpdateStatusBanner();

                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Combined PO Created",
                        $"Combined PO {poTemplate.PoNo} created across {selected.Count} requisitions.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

    }
}
