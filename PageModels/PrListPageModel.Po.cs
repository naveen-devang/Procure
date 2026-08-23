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
    // Inline PO operations: the two-step wizard, edit mode and quantity validation.
    public partial class PrListPageModel
    {
        // Multi-RFQ Add/Edit PO modal state (Two-Step Wizard with Skeleton Loading & Quantity Validation)
        [ObservableProperty]
        public partial bool IsAddPoModalVisible { get; set; }

        [ObservableProperty]
        public partial bool IsEditPoMode { get; set; }

        [ObservableProperty]
        public partial PurchaseOrder? EditingPo { get; set; }

        [ObservableProperty]
        public partial string PoModalTitle { get; set; } = "Raise Purchase Order (PO)";

        [ObservableProperty]
        public partial string PoModalSaveButtonText { get; set; } = "Raise Purchase Order(s)";

        [ObservableProperty]
        public partial string PoModalBackButtonText { get; set; } = "← Back to Quotes";

        [ObservableProperty]
        public partial int PoModalCurrentStep { get; set; } = 1;

        public bool IsPoModalStep1 => PoModalCurrentStep == 1;
        public bool IsPoModalStep2 => PoModalCurrentStep == 2;

        [ObservableProperty]
        public partial bool IsPoModalStep1Loading { get; set; }

        [ObservableProperty]
        public partial bool IsPoModalStep2Loading { get; set; }

        [ObservableProperty]
        public partial bool CanGoToPoStep2 { get; set; }

        [ObservableProperty]
        public partial bool HasPoQuantityValidationErrors { get; set; }

        [ObservableProperty]
        public partial string PoQuantityValidationErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string PoAllocationSummaryText { get; set; } = string.Empty;

        partial void OnPoModalCurrentStepChanged(int value)
        {
            OnPropertyChanged(nameof(IsPoModalStep1));
            OnPropertyChanged(nameof(IsPoModalStep2));
        }

        [ObservableProperty]
        public partial PurchaseRequisition? TargetPrForPo { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<PoRfqSelection> PoRfqSelections { get; set; } = new();

        [ObservableProperty]
        public partial int SelectedPoRfqCount { get; set; }

        [ObservableProperty]
        public partial string SelectedPoRfqCountMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string TotalPoAmountSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string AddPoModalSubtitle { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasPoRfqs { get; set; }


        // ================= PO INLINE OPERATIONS (MULTI-STEP WIZARD, EDIT MODE & QUANTITY VALIDATION) =================

        [RelayCommand]
        public async Task OpenAddPoModalAsync(PurchaseRequisition pr)
        {
            TargetPrForPo = pr;
            IsEditPoMode = false;
            EditingPo = null;
            PoModalTitle = "Raise Purchase Order (PO)";
            PoModalSaveButtonText = "Raise Purchase Order(s)";
            PoModalBackButtonText = "← Back to Quotes";
            PoModalCurrentStep = 1;
            IsPoModalStep1Loading = true;
            IsPoModalStep2Loading = false;
            AddPoModalSubtitle = $"PR: {pr.PrNo} • {(pr.Rfqs?.Count ?? 0)} Supplier Quote(s) available";
            IsAddPoModalVisible = true;

            // Cleanup previous listeners if any
            if (PoRfqSelections != null)
            {
                foreach (var s in PoRfqSelections)
                {
                    s.OnTotalsRecalculated = null;
                    if (s.Items != null)
                    {
                        foreach (var itm in s.Items)
                        {
                            itm.OnPriceOrSelectionChanged = null;
                        }
                    }
                }
            }

            PoRfqSelections = new ObservableCollection<PoRfqSelection>();

            if (pr.Rfqs != null && pr.Rfqs.Count > 0)
            {
                HasPoRfqs = true;

                for (int i = 0; i < pr.Rfqs.Count; i++)
                {
                    var rfq = pr.Rfqs[i];
                    var existingPo = pr.Pos?.FirstOrDefault(p => (p.LinkedRfqId.HasValue && p.LinkedRfqId.Value == rfq.Id) || (!string.IsNullOrWhiteSpace(p.Vendor) && !string.IsNullOrWhiteSpace(rfq.Vendor) && string.Equals(p.Vendor.Trim(), rfq.Vendor.Trim(), StringComparison.OrdinalIgnoreCase)));

                    PoRfqSelection selection;
                    if (existingPo != null)
                    {
                        selection = new PoRfqSelection(existingPo, rfq, pr)
                        {
                            // Pre-checked cards silently re-raised (duplicated) the existing PO on
                            // save; the user opts back in to update it instead.
                            IsSelected = false,
                            OnTotalsRecalculated = RecalculatePoModalTotals
                        };
                    }
                    else
                    {
                        selection = new PoRfqSelection(rfq, pr, isSelected: true)
                        {
                            PoNo = string.Empty,
                            OnTotalsRecalculated = RecalculatePoModalTotals
                        };
                    }

                    selection.NotifyCalculationsChanged();
                    PoRfqSelections.Add(selection);
                }
            }
            else
            {
                HasPoRfqs = false;
            }

            RecalculatePoModalTotals();

            // Smooth skeleton loading display
            await Task.Delay(180);
            IsPoModalStep1Loading = false;
        }

        [RelayCommand]
        public async Task OpenEditPoModalAsync(PurchaseOrder po)
        {
            if (po == null) return;

            // Find parent PR
            var pr = _loadedPrs.FirstOrDefault(p => p.Id == po.PrId) ?? FilteredPrs.FirstOrDefault(p => p.Id == po.PrId);
            if (pr == null) return;

            TargetPrForPo = pr;
            IsEditPoMode = true;
            EditingPo = po;
            PoModalTitle = $"Edit Purchase Order ({po.PoNo})";
            PoModalSaveButtonText = "Save PO Changes";
            PoModalBackButtonText = "Cancel";
            PoModalCurrentStep = 2; // Jump directly to Step 2
            IsPoModalStep1Loading = false;
            IsPoModalStep2Loading = true;
            AddPoModalSubtitle = $"Editing PO for PR: {pr.PrNo} • Vendor: {po.Vendor}";
            IsAddPoModalVisible = true;

            // Cleanup previous listeners if any
            if (PoRfqSelections != null)
            {
                foreach (var s in PoRfqSelections)
                {
                    s.OnTotalsRecalculated = null;
                    if (s.Items != null)
                    {
                        foreach (var itm in s.Items)
                        {
                            itm.OnPriceOrSelectionChanged = null;
                        }
                    }
                }
            }

            PoRfqSelections = new ObservableCollection<PoRfqSelection>();
            HasPoRfqs = true;

            var linkedRfq = pr.Rfqs?.FirstOrDefault(r => r.Id == po.LinkedRfqId);
            var selection = new PoRfqSelection(po, linkedRfq, pr)
            {
                OnTotalsRecalculated = RecalculatePoModalTotals
            };

            PoRfqSelections.Add(selection);

            RecalculatePoModalTotals();

            await Task.Delay(150);
            IsPoModalStep2Loading = false;
        }

        public void RecalculatePoModalTotals()
        {
            if (PoRfqSelections == null || PoRfqSelections.Count == 0)
            {
                SelectedPoRfqCount = 0;
                SelectedPoRfqCountMessage = "No RFQ available";
                TotalPoAmountSummary = string.Empty;
                CanGoToPoStep2 = false;
                HasPoQuantityValidationErrors = false;
                PoQuantityValidationErrorMessage = string.Empty;
                PoAllocationSummaryText = string.Empty;
                return;
            }

            var selected = PoRfqSelections.Where(r => r.IsSelected).ToList();
            SelectedPoRfqCount = selected.Count;
            CanGoToPoStep2 = selected.Count > 0;
            SelectedPoRfqCountMessage = $"{selected.Count} of {PoRfqSelections.Count} supplier quote(s) selected";

            if (selected.Count == 0)
            {
                TotalPoAmountSummary = "0 quotes selected";
            }
            else
            {
                var curGroups = selected
                    .GroupBy(s => string.IsNullOrWhiteSpace(s.Currency) ? "AED" : s.Currency)
                    .Select(g => MoneyFormat.Format(g.Key, g.Sum(s => s.DisplayTotalAmount)));
                TotalPoAmountSummary = string.Join("  •  ", curGroups);
            }

            // Cross-PO Multi-Supplier Item Allocation Validation
            HasPoQuantityValidationErrors = false;
            PoQuantityValidationErrorMessage = string.Empty;

            if (TargetPrForPo != null && TargetPrForPo.Items != null && TargetPrForPo.Items.Count > 0)
            {
                var allSelectedModalItems = selected.SelectMany(s => s.Items?.Where(i => i.IsSelected) ?? Enumerable.Empty<PoRfqItemSelection>()).ToList();
                int balancedItemsCount = 0;
                int pendingItemsCount = 0;
                int overAllocatedItemsCount = 0;
                var pendingItemsList = new List<string>();

                // A selected card that updates an existing PO replaces that PO's quantities, so
                // they must not also be counted as "other PO" — counting both double-allocated
                // every item and reported a false "Exceeds PR target" on reopen.
                var updatingPoIds = IsEditPoMode && EditingPo != null
                    ? new HashSet<Guid> { EditingPo.Id }
                    : new HashSet<Guid>(selected.Where(s => s.EditingPoId.HasValue).Select(s => s.EditingPoId!.Value));

                foreach (var prItem in TargetPrForPo.Items)
                {
                    var otherPoOrdered = TargetPrForPo.Pos
                        .Where(p => !updatingPoIds.Contains(p.Id))
                        .SelectMany(p => p.Items ?? Enumerable.Empty<PurchaseOrderItem>())
                        .Where(pi => (pi.PrItemId.HasValue && pi.PrItemId.Value == prItem.Id) || string.Equals(pi.ItemName, prItem.ItemName, StringComparison.OrdinalIgnoreCase))
                        .Sum(pi => pi.Quantity);

                    var totalInModalForItem = allSelectedModalItems
                        .Where(mi => (mi.PrItemId.HasValue && mi.PrItemId.Value == prItem.Id) || string.Equals(mi.ItemName, prItem.ItemName, StringComparison.OrdinalIgnoreCase))
                        .Sum(mi => mi.Quantity);

                    var totalAllocated = otherPoOrdered + totalInModalForItem;

                    if (totalAllocated > prItem.Quantity)
                    {
                        var excess = totalAllocated - prItem.Quantity;
                        HasPoQuantityValidationErrors = true;
                        PoQuantityValidationErrorMessage = $"Quantity for '{prItem.ItemName}' exceeds PR target by {excess:G29} {prItem.Unit} (Allocated: {totalAllocated:G29}, Target: {prItem.Quantity:G29})";
                        overAllocatedItemsCount++;
                    }
                    else if (totalAllocated == prItem.Quantity && prItem.Quantity > 0)
                    {
                        balancedItemsCount++;
                    }
                    else
                    {
                        var pending = Math.Max(0m, prItem.Quantity - totalAllocated);
                        pendingItemsCount++;
                        var unitStr = string.IsNullOrWhiteSpace(prItem.Unit) ? "pcs" : prItem.Unit;
                        pendingItemsList.Add($"{prItem.ItemName} ({pending:G29} {unitStr} pending)");
                    }
                }

                if (HasPoQuantityValidationErrors)
                {
                    PoAllocationSummaryText = $"Over-allocated: {PoQuantityValidationErrorMessage}";
                }
                else if (pendingItemsCount == 0 && balancedItemsCount > 0)
                {
                    PoAllocationSummaryText = $"All {balancedItemsCount} items complete";
                }
                else if (TargetPrForPo.Items.Count > 0)
                {
                    PoAllocationSummaryText = $"{balancedItemsCount}/{TargetPrForPo.Items.Count} items complete ({pendingItemsCount} pending)";
                }
                else
                {
                    PoAllocationSummaryText = string.Empty;
                }
            }
            else
            {
                PoAllocationSummaryText = string.Empty;
            }
        }

        [RelayCommand]
        public void SelectAllPoRfqs()
        {
            if (PoRfqSelections == null) return;
            foreach (var sel in PoRfqSelections)
            {
                sel.IsSelected = true;
            }
            RecalculatePoModalTotals();
        }

        [RelayCommand]
        public void DeselectAllPoRfqs()
        {
            if (PoRfqSelections == null) return;
            foreach (var sel in PoRfqSelections)
            {
                sel.IsSelected = false;
            }
            RecalculatePoModalTotals();
        }

        [RelayCommand]
        public void SelectAllPoRfqItems(PoRfqSelection? sel)
        {
            sel?.SelectAllItems();
            RecalculatePoModalTotals();
        }

        [RelayCommand]
        public void DeselectAllPoRfqItems(PoRfqSelection? sel)
        {
            sel?.DeselectAllItems();
            RecalculatePoModalTotals();
        }

        [RelayCommand]
        public async Task GoToPoStep2Async()
        {
            var selected = PoRfqSelections?.Where(r => r.IsSelected).ToList();
            if (selected == null || selected.Count == 0)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Please select at least one supplier quote to configure PO.", "OK");
                return;
            }

            IsPoModalStep2Loading = true;
            PoModalCurrentStep = 2;
            RecalculatePoModalTotals();

            // Smooth transition animation
            await Task.Delay(150);
            IsPoModalStep2Loading = false;
        }

        [RelayCommand]
        public void GoToPoStep1()
        {
            if (IsEditPoMode)
            {
                CloseAddPoModal();
                return;
            }
            PoModalCurrentStep = 1;
            if (PoRfqSelections != null)
            {
                foreach (var s in PoRfqSelections)
                {
                    s.NotifyCalculationsChanged();
                }
            }
            RecalculatePoModalTotals();
        }

        [RelayCommand]
        public async Task SaveNewPoAsync()
        {
            if (TargetPrForPo == null) return;

            var selectedRfqs = PoRfqSelections?.Where(r => r.IsSelected).ToList();
            if (selectedRfqs == null || selectedRfqs.Count == 0)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Please select at least one RFQ / Supplier quote to raise a PO.", "OK");
                return;
            }

            // Check for over-allocation errors
            if (HasPoQuantityValidationErrors)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Quantity Over-allocation", PoQuantityValidationErrorMessage, "OK");
                return;
            }

            // Validate PO Numbers
            foreach (var rfqSel in selectedRfqs)
            {
                if (string.IsNullOrWhiteSpace(rfqSel.PoNo))
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Validation", $"Please enter a PO Number for {rfqSel.VendorName}.", "OK");
                    return;
                }
            }

            try
            {
                // The PO save deliberately never writes back into the linked RFQ: the RFQ is the
                // record of what the vendor quoted, and pushing PO allocations/deselections into
                // it falsified every later PCR export and quote comparison.
                if (IsEditPoMode && EditingPo != null)
                {
                    // EDIT EXISTING PO MODE
                    ApplySelectionToPo(selectedRfqs[0], EditingPo, freshItemIds: false);
                    await _prRepo.SavePoAsync(EditingPo);
                }
                else
                {
                    // Two same-vendor quote cards can resolve to the same existing PO; saving both
                    // would silently overwrite the first card's data with the second's.
                    var duplicateTarget = selectedRfqs
                        .Where(s => s.EditingPoId.HasValue)
                        .GroupBy(s => s.EditingPoId!.Value)
                        .FirstOrDefault(g => g.Count() > 1);
                    if (duplicateTarget != null)
                    {
                        if (Shell.Current != null)
                            await Shell.Current.DisplayAlertAsync("Duplicate Target", $"Two selected quotes would update the same existing PO ({duplicateTarget.First().VendorName}). Please deselect one of them.", "OK");
                        return;
                    }

                    // CREATE NEW PO(S) MODE
                    foreach (var rfqSel in selectedRfqs)
                    {
                        var existing = rfqSel.EditingPoId.HasValue
                            ? TargetPrForPo.Pos.FirstOrDefault(p => p.Id == rfqSel.EditingPoId.Value)
                            : null;

                        if (existing != null)
                        {
                            // Card was inflated from an already-raised PO; saving updates it
                            // rather than inserting a second PO with the same number.
                            ApplySelectionToPo(rfqSel, existing, freshItemIds: false);
                            await _prRepo.SavePoAsync(existing);
                        }
                        else
                        {
                            var po = new PurchaseOrder
                            {
                                Id = Guid.NewGuid(),
                                PrId = TargetPrForPo.Id,
                                LinkedRfqId = rfqSel.Rfq?.Id,
                                Status = PoStatus.Raised,
                                Date = DateTime.Today
                            };
                            ApplySelectionToPo(rfqSel, po, freshItemIds: true);
                            await _prRepo.SavePoAsync(po);
                            TargetPrForPo.Pos.Add(po);
                            // A retry after a later card fails must update this PO, not insert a
                            // second one with the same number.
                            rfqSel.EditingPoId = po.Id;
                        }
                    }

                    // Update PR status to PO Raised if applicable
                    if (TargetPrForPo.Status == ProcurementStatus.PcrApproved ||
                        TargetPrForPo.Status == ProcurementStatus.PcrSubmitted ||
                        TargetPrForPo.Status == ProcurementStatus.QuotesReceived ||
                        TargetPrForPo.Status == ProcurementStatus.PrRaised)
                    {
                        TargetPrForPo.Status = ProcurementStatus.PoRaised;
                        await _prRepo.SavePrFieldsAsync(TargetPrForPo);
                    }
                }

                TargetPrForPo.NotifyHierarchyChanged();
                UpdateStatusBanner();
                CloseAddPoModal();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // Single mapping from a wizard card to a PurchaseOrder, shared by create, update-existing
        // and edit-mode saves so the three paths cannot drift apart. freshItemIds must be true for
        // a brand-new PO: the wizard row Ids are minted once per modal open, and reusing them on a
        // retry after a partial failure would re-parent the first save's item rows onto the
        // duplicate via the repository's ON CONFLICT(Id) upsert.
        private static void ApplySelectionToPo(PoRfqSelection rfqSel, PurchaseOrder po, bool freshItemIds)
        {
            po.PoNo = rfqSel.PoNo.Trim();
            po.Vendor = rfqSel.VendorName.Trim();
            po.Value = rfqSel.DisplayTotalAmount;
            po.Currency = string.IsNullOrWhiteSpace(rfqSel.Currency) ? "AED" : rfqSel.Currency;
            po.BaseAmount = rfqSel.BaseAmount;
            po.Freight = rfqSel.Freight;
            po.OtherCharges = rfqSel.OtherCharges;
            po.Discount = rfqSel.OverallDiscount;
            po.VatType = rfqSel.VatType;

            po.Items.Clear();
            if (rfqSel.HasItems)
            {
                foreach (var itemSel in rfqSel.Items.Where(i => i.IsSelected))
                {
                    po.Items.Add(new PurchaseOrderItem
                    {
                        Id = freshItemIds ? Guid.NewGuid() : itemSel.Id,
                        PoId = po.Id,
                        PrItemId = itemSel.PrItemId,
                        RfqItemId = itemSel.RfqItemId,
                        ItemName = itemSel.ItemName,
                        Quantity = itemSel.Quantity,
                        Unit = itemSel.Unit,
                        UnitPrice = itemSel.QuotedUnitPrice,
                        Discount = itemSel.Discount
                    });
                }
            }
        }

        [RelayCommand]
        public void CloseAddPoModal()
        {
            if (PoRfqSelections != null)
            {
                foreach (var s in PoRfqSelections)
                {
                    s.OnTotalsRecalculated = null;
                    if (s.Items != null)
                    {
                        foreach (var itm in s.Items)
                        {
                            itm.OnPriceOrSelectionChanged = null;
                        }
                    }
                }
            }
            IsAddPoModalVisible = false;
            IsEditPoMode = false;
            EditingPo = null;
        }

        [RelayCommand]
        public async Task UpdatePoStatusAsync(PurchaseOrder po)
        {
            if (Shell.Current == null) return;

            var action = await Shell.Current.DisplayActionSheetAsync(
                $"Update Status for {po.PoNo}",
                "Cancel",
                null,
                PoStatus.Raised,
                PoStatus.Delivered,
                PoStatus.Closed);

            if (action == null || action == "Cancel" || action == po.Status) return;

            try
            {
                po.Status = action;
                await _prRepo.SavePoAsync(po);

                var parentPr = _loadedPrs.FirstOrDefault(p => p.Id == po.PrId);
                if (parentPr != null)
                {
                    if (parentPr.Pos.All(p => p.Status == PoStatus.Delivered))
                    {
                        parentPr.Status = ProcurementStatus.Delivered;
                        await _prRepo.SavePrFieldsAsync(parentPr);
                    }
                    parentPr.NotifyHierarchyChanged();
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task DeletePoAsync(PurchaseOrder po)
        {
            if (Shell.Current == null) return;

            var confirm = await Shell.Current.DisplayAlertAsync("Delete PO", $"Delete PO {po.PoNo}?", "Delete", "Cancel");
            if (!confirm) return;

            try
            {
                await _prRepo.DeletePoAsync(po.Id);
                var parentPr = _loadedPrs.FirstOrDefault(p => p.Id == po.PrId);
                if (parentPr != null)
                {
                    parentPr.Pos.Remove(po);
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
