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
using Procure.Abstractions;
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
        public partial string NewRfqTechnicalApproval { get; set; } = TechnicalApprovalNotSet;

        // Blank first entry = "no technical approval recorded". Stored as an empty string, which
        // both PCR exporters already render as "-". Shared by the Add RFQ and Batch RFQ pickers.
        // "Not set" rather than an empty first entry: a ComboBox whose selected item is an empty
        // string leaves its ContentPresenter with no content, and a ContentPresenter with null
        // content renders its DataContext - the box read "Procure.PageModels.PrListPageModel".
        // Stored as an empty string either way; see TechnicalApprovalNotSet.
        public const string TechnicalApprovalNotSet = "Not set";
        public List<string> AvailableTechnicalApprovals { get; } = new() { TechnicalApprovalNotSet, "Approved", "Not Approved" };

        private static string StoredApproval(string? shown) =>
            string.IsNullOrWhiteSpace(shown) || shown == TechnicalApprovalNotSet ? string.Empty : shown.Trim();

        private static string ShownApproval(string? stored) =>
            string.IsNullOrWhiteSpace(stored) ? TechnicalApprovalNotSet : stored;

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

        // Distinguishes the recalc's own item-sum sync from a user-typed lump sum: after the sync
        // has driven NewRfqQuoteAmount, clearing every price must zero the total instead of
        // falling back to the stale synced figure (which then saved as a phantom quote).
        private bool _rfqQuoteAmountAutoSynced;
        private bool _syncingRfqQuoteAmount;

        partial void OnNewRfqQuoteAmountChanged(decimal? value)
        {
            if (_syncingRfqQuoteAmount)
            {
                // The recalc itself is writing the synced sum; re-entering it doubled every pass.
                return;
            }
            _rfqQuoteAmountAutoSynced = false;
            RecalculateRfqTotals();
        }
        partial void OnNewRfqFreightChanged(decimal? value) => RecalculateRfqTotals();
        partial void OnNewRfqOtherChargesChanged(decimal? value) => RecalculateRfqTotals();
        partial void OnNewRfqDiscountChanged(decimal? value) => RecalculateRfqTotals();
        partial void OnNewRfqVatTypeChanged(string value) => RecalculateRfqTotals();
        partial void OnNewRfqCurrencyChanged(string value) => RecalculateRfqTotals();

        public IReadOnlyList<string> AvailableCurrencies => AppConstants.SupportedCurrencies;
        public List<string> AvailableVatTypes { get; } = new() { "5%", "RC", "V0" };
        // Incoterms 2020 in ICC order - the seven any-mode rules, then the four sea and inland
        // waterway ones - followed by two withdrawn terms vendors here still write on quotes.
        // DDU was replaced by DAP in the 2010 revision; DAT was renamed DPU in 2020. Kept so a
        // quote that says DDU can be recorded as it stands, not silently rewritten.
        public List<string> AvailableIncoterms { get; } = new()
        {
            "EXW", "FCA", "CPT", "CIP", "DAP", "DPU", "DDP",   // any mode of transport
            "FAS", "FOB", "CFR", "CIF",                         // sea / inland waterway
            "DDU", "DAT",                                       // withdrawn, still seen on paperwork
        };


        // ================= RFQ INLINE OPERATIONS =================

        public void RecalculateRfqTotals()
        {
            if (EditingRfqItems != null && EditingRfqItems.Count > 0)
            {
                var quotedSum = EditingRfqItems.Where(i => i.IsQuoted).Sum(i => i.LineTotal);
                if (quotedSum > 0)
                {
                    CalculatedRfqBaseTotal = quotedSum;
                    _syncingRfqQuoteAmount = true;
                    NewRfqQuoteAmount = quotedSum;
                    _syncingRfqQuoteAmount = false;
                    _rfqQuoteAmountAutoSynced = true;
                }
                else if (_rfqQuoteAmountAutoSynced)
                {
                    // The lump sum was machine-written from the items; with every price cleared
                    // the items are authoritative again, so the total drops to zero.
                    CalculatedRfqBaseTotal = 0m;
                    _syncingRfqQuoteAmount = true;
                    NewRfqQuoteAmount = null;
                    _syncingRfqQuoteAmount = false;
                    _rfqQuoteAmountAutoSynced = false;
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
            OnPropertyChanged(nameof(AllRfqItemsSelected));
        }

        public bool AllRfqItemsSelected => EditingRfqItems is { Count: > 0 } && EditingRfqItems.All(i => i.IsQuoted);

        private void OnEditingRfqItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // LineTotal is only ever raised as a companion of the four source properties below;
            // matching it too ran the recalc twice for every edit.
            if (e.PropertyName == nameof(RfqItem.IsQuoted) ||
                e.PropertyName == nameof(RfqItem.QuotedUnitPrice) ||
                e.PropertyName == nameof(RfqItem.Discount) ||
                e.PropertyName == nameof(RfqItem.Quantity))
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
        public void ToggleAllRfqItems()
        {
            if (AllRfqItemsSelected) DeselectAllRfqItems();
            else SelectAllRfqItems();
        }

        /// <summary>Quote lines the user typed in during THIS editing session. Only these get the
        /// "this isn't on the requisition - add it?" question at save; asking about every unlinked
        /// line would re-ask on every save for a line already declined once.</summary>
        private readonly HashSet<Guid> _rfqLinesAddedThisSession = new();

        [RelayCommand]
        public void AddEditingRfqItem()
        {
            var item = new RfqItem
            {
                Id = Guid.NewGuid(),
                RfqId = EditingRfq?.Id ?? Guid.Empty,
                PrItemId = null, // split / extra line, no direct PR origin
                ItemName = string.Empty,
                Quantity = 1,
                Unit = "pcs",
                IsQuoted = true,
                SortOrder = EditingRfqItems.Count
            };
            item.PropertyChanged += OnEditingRfqItemPropertyChanged;
            _rfqLinesAddedThisSession.Add(item.Id);
            EditingRfqItems.Add(item);
            RecalculateRfqTotals();
        }

        [RelayCommand]
        public async Task RemoveEditingRfqItemAsync(RfqItem item)
        {
            // Keep at least one line: an RFQ item table with zero rows is meaningless (lump-sum
            // quoting is the separate quote-amount field).
            if (EditingRfqItems.Count <= 1) return;

            // A PO line points back at the quote line it was priced from. Dropping the quote line
            // silently cut that reference, so the order could no longer show what it was based on.
            var orderedBy = TargetPrForRfq?.Pos?
                .Where(po => po.Items != null && po.Items.Any(pi => pi.RfqItemId.HasValue && pi.RfqItemId.Value == item.Id))
                .Select(po => po.PoNo)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();

            if (orderedBy is { Count: > 0 })
            {
                {
                    await _dialogs.DisplayAlertAsync(
                        "Line Already Ordered",
                        $"'{item.ItemName}' was ordered on {string.Join(", ", orderedBy)}. Edit or cancel that PO first, then remove the line.",
                        "OK");
                }
                return;
            }

            if (EditingRfqItems.Remove(item))
            {
                item.PropertyChanged -= OnEditingRfqItemPropertyChanged;
                RecalculateRfqTotals();
            }
        }

        [RelayCommand]
        public async Task CopySelectedRfqItemsForEmailAsync()
        {
            if (EditingRfqItems == null || EditingRfqItems.Count == 0)
            {
                    await _dialogs.DisplayAlertAsync("No Items", "There are no items in this RFQ to copy.", "OK");
                return;
            }

            var selectedItems = EditingRfqItems.Where(i => i.IsQuoted && !string.IsNullOrWhiteSpace(i.ItemName)).ToList();
            if (selectedItems.Count == 0)
            {
                    await _dialogs.DisplayAlertAsync("No Selected Items", "Please select at least one item to copy for email.", "OK");
                return;
            }

            try
            {
                await RfqClipboardFormatter.CopyToClipboardAsync(selectedItems);
                ShowToast($"Copied {selectedItems.Count} item(s) for email");
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
                    // A blank cell in the pasted column explicitly un-quotes the row; treating it
                    // as absent shifted every following price onto the wrong item.
                    targetItem.IsQuoted = row.UnitPrice.HasValue;
                }

                if (row.HasDiscount)
                {
                    targetItem.Discount = ResolvePastedDiscount(row, targetItem);
                }

                if (row.HasLastPrice)
                {
                    targetItem.LastPrice = row.LastPrice;
                }
            }

            RecalculateRfqTotals();
        }

        // "5%" pasted into the per-unit discount column carries a percentage; storing the bare
        // number applied it as 5 currency units. Convert against the row's unit price, or drop
        // the value when no price is available to convert with.
        private static decimal? ResolvePastedDiscount(RfqPricingPasteRow row, RfqItem targetItem)
        {
            if (!row.Discount.HasValue || !row.DiscountIsPercent)
            {
                return row.Discount;
            }
            var unitPrice = row.HasUnitPrice ? row.UnitPrice : targetItem.QuotedUnitPrice;
            return unitPrice.HasValue ? Math.Round(unitPrice.Value * row.Discount.Value / 100m, 2) : null;
        }

        [RelayCommand]
        public void OpenAddRfqModal(PurchaseRequisition pr)
        {
            EditingRfq = null;
            IsEditingRfq = false;
            _rfqQuoteAmountAutoSynced = false;
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
            NewRfqTechnicalApproval = TechnicalApprovalNotSet;

            foreach (var item in EditingRfqItems)
                item.PropertyChanged -= OnEditingRfqItemPropertyChanged;

            EditingRfqItems.Clear();
            _rfqLinesAddedThisSession.Clear();

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
            _rfqQuoteAmountAutoSynced = false;
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
            NewRfqTechnicalApproval = ShownApproval(rfq.TechnicalApproval);

            foreach (var item in EditingRfqItems)
                item.PropertyChanged -= OnEditingRfqItemPropertyChanged;

            EditingRfqItems.Clear();
            _rfqLinesAddedThisSession.Clear();

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
                    await _dialogs.DisplayAlertAsync("Validation", "Please enter an RFQ Number.", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(NewRfqVendor))
            {
                    await _dialogs.DisplayAlertAsync("Validation", "Please enter a Vendor name.", "OK");
                return;
            }

            // Drop unused "+ Add Line" rows the user never filled in.
            var savedRfqItems = EditingRfqItems.Where(i => !string.IsNullOrWhiteSpace(i.ItemName)).ToList();

            // A line typed straight into the quote used to reach nowhere: it priced, it ordered, and
            // the requisition never learned it existed, so the PR's pending figure stayed short by
            // that quantity for ever. Offer to put it on the PR before anything is written.
            await ReconcileNewQuoteLinesToPrAsync(savedRfqItems);

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
                    EditingRfq.TechnicalApproval = StoredApproval(NewRfqTechnicalApproval);

                    // Update items
                    EditingRfq.Items = new ObservableCollection<RfqItem>(savedRfqItems);

                    if ((EditingRfq.QuoteAmount.HasValue && EditingRfq.QuoteAmount.Value > 0) || (EditingRfq.HasLineItems && EditingRfq.QuotedItemsCount > 0 && EditingRfq.BaseAmount > 0))
                    {
                        if (EditingRfq.Status == RfqStatus.Sent)
                        {
                            EditingRfq.Status = RfqStatus.QuoteReceived;
                            EditingRfq.QuoteReceivedDate = DateTime.Today;
                        }
                    }
                    else if (EditingRfq.Status == RfqStatus.QuoteReceived)
                    {
                        // Clearing every price returns the RFQ to awaiting-quote; the one-way
                        // escalation previously left a phantom QuoteReceived state behind.
                        EditingRfq.Status = RfqStatus.Sent;
                        EditingRfq.QuoteReceivedDate = null;
                    }

                    await _prRepo.SaveRfqAsync(EditingRfq);
                    EditingRfq.NotifyCalculationsChanged();
                    TargetPrForRfq.NotifyHierarchyChanged();
                    DataChangeNotifier.Notify(ProcurementChange.Rfq);

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
                    TechnicalApproval = StoredApproval(NewRfqTechnicalApproval),
                    SentDate = DateTime.Today,
                    Items = new ObservableCollection<RfqItem>(savedRfqItems)
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
                DataChangeNotifier.Notify(ProcurementChange.Rfq);

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

        /// <summary>Offers to put quote lines the requisition does not have onto the requisition.
        /// Only asks about lines added in this session, and only once - decline and the line simply
        /// stays unlinked, which the PO window then shows as an extra rather than inventing a
        /// requisition target for it.</summary>
        private async Task ReconcileNewQuoteLinesToPrAsync(List<RfqItem> lines)
        {
            var pr = TargetPrForRfq;
            if (pr == null || false || _rfqLinesAddedThisSession.Count == 0) return;

            var matched = PrLineMatcher.Map(lines, pr.Items);
            var strays = lines
                .Where(l => _rfqLinesAddedThisSession.Contains(l.Id) && !matched.ContainsKey(l))
                .ToList();

            if (strays.Count == 0) return;

            var listing = string.Join("\n", strays.Take(5).Select(l => $"• {l.ItemName} ({l.FormattedQuantity})"));
            if (strays.Count > 5) listing += $"\n• …and {strays.Count - 5} more";

            var add = await _dialogs.DisplayAlertAsync(
                strays.Count == 1 ? "Line Not On The Requisition" : "Lines Not On The Requisition",
                $"{listing}\n\nAdd to requisition {pr.PrNo}? Without this the quantity is quoted and ordered but never counted against the PR.",
                "Add to PR",
                "Keep as extra");

            // Answered either way, so stop asking about these on the next save.
            foreach (var stray in strays) _rfqLinesAddedThisSession.Remove(stray.Id);

            if (!add) return;

            var sortOrder = pr.Items.Count == 0 ? 0 : pr.Items.Max(i => i.SortOrder) + 1;
            foreach (var stray in strays)
            {
                var prItem = new PrItem
                {
                    Id = Guid.NewGuid(),
                    PrId = pr.Id,
                    ItemName = stray.ItemName,
                    Quantity = stray.Quantity,
                    Unit = stray.Unit,
                    EstimatedUnitPrice = stray.LastPrice,
                    SortOrder = sortOrder++
                };
                pr.Items.Add(prItem);
                stray.PrItemId = prItem.Id;
            }

            await _prRepo.SaveAsync(pr);
            DataChangeNotifier.Notify(ProcurementChange.Pr);
            ShowToast(strays.Count == 1
                ? $"Added 1 item to {pr.PrNo}"
                : $"Added {strays.Count} items to {pr.PrNo}");
        }

        [RelayCommand]
        public void CloseAddRfqModal()
        {
            foreach (var item in EditingRfqItems)
                item.PropertyChanged -= OnEditingRfqItemPropertyChanged;

            EditingRfqItems.Clear();
            _rfqLinesAddedThisSession.Clear();
            IsAddRfqModalVisible = false;
            EditingRfq = null;
            IsEditingRfq = false;
        }

        [RelayCommand]
        public async Task MarkQuoteReceivedAsync(RequestForQuotation rfq)
        {

            var amountStr = await _dialogs.DisplayPromptAsync(
                "Quote Received",
                $"Enter quote amount for {rfq.Vendor}:",
                "Save",
                "Cancel",
                "Quote Amount (e.g. 5000)");

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

            // A PO raised from this quote keeps its money but loses its provenance when the quote
            // goes: its Edit screen can no longer show the terms it was built on, and the price
            // comparison loses that column. Allowed, but not without saying so.
            var parent = LoadedPrs.FirstOrDefault(p => p.Id == rfq.PrId);
            var dependentPos = parent?.Pos?
                .Where(p => p.LinkedRfqId.HasValue && p.LinkedRfqId.Value == rfq.Id)
                .Select(p => p.PoNo)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();

            var confirm = dependentPos is { Count: > 0 }
                ? await _dialogs.DisplayAlertAsync(
                    "Delete Quoted RFQ",
                    $"Purchase order {string.Join(", ", dependentPos)} was raised from this quote.\n\nDeleting it keeps the order and its value, but the order loses the commercial terms it was built on and drops out of the price comparison.",
                    "Delete anyway",
                    "Cancel")
                : await _dialogs.DisplayAlertAsync("Delete RFQ", $"Delete RFQ for {rfq.Vendor}?", "Delete", "Cancel");

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
                DataChangeNotifier.Notify(ProcurementChange.Rfq);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

    }
}
