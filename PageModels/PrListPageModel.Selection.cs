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
    // Multi-PR selection: the dynamic toolbar, consolidation / merge, and the
    // split-PR, split-shared-RFQ and split-combined-PO routes.
    public partial class PrListPageModel
    {
        // Batch selection & actions state
        [ObservableProperty]
        public partial int SelectedPrsCount { get; set; }

        [ObservableProperty]
        public partial bool IsBatchSelectionActive { get; set; }

        [ObservableProperty]
        public partial bool CanMergeSelectedPrs { get; set; }

        [ObservableProperty]
        public partial string SelectedPrsSummary { get; set; } = string.Empty;

        // Dynamic toolbar buttons state
        [ObservableProperty]
        public partial string MergeButtonText { get; set; } = "Combine / Merge PRs";

        [ObservableProperty]
        public partial string MergeButtonToolTip { get; set; } = "Merge selected requisitions into a single combined PR";

        [ObservableProperty]
        public partial bool IsMergeOrSplitPrEnabled { get; set; }

        [ObservableProperty]
        public partial string SharedRfqButtonText { get; set; } = "Shared RFQ";

        [ObservableProperty]
        public partial string SharedRfqButtonToolTip { get; set; } = "Create a shared RFQ for selected requisitions";

        [ObservableProperty]
        public partial bool IsSharedRfqActionEnabled { get; set; }

        [ObservableProperty]
        public partial string CombinedPoButtonText { get; set; } = "Combined PO";

        [ObservableProperty]
        public partial string CombinedPoButtonToolTip { get; set; } = "Create a combined purchase order across selected requisitions";

        [ObservableProperty]
        public partial bool IsCombinedPoActionEnabled { get; set; }

        // Consolidation / Merge PR modal state
        [ObservableProperty]
        public partial bool IsMergePrModalVisible { get; set; }

        [ObservableProperty]
        public partial string MergeMasterPrNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MergeDescription { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MergeRequestor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MergePriority { get; set; } = ProcurementPriority.Normal;

        [ObservableProperty]
        public partial string MergeNotes { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool MergeCopyChildRfqs { get; set; } = true;

        [ObservableProperty]
        public partial ObservableCollection<PurchaseRequisition> SelectedPrsForMerge { get; set; } = new();

        // Selective Split PR modal state
        [ObservableProperty]
        public partial bool IsSplitPrModalVisible { get; set; }

        [ObservableProperty]
        public partial PurchaseRequisition? SplitTargetMasterPr { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<SplitPrEntry> SplitPrEntries { get; set; } = new();

        [ObservableProperty]
        public partial string SplitActionSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool CanConfirmSplit { get; set; } = true;


        // ================= BATCH SELECTION & ACTIONS =================

        [RelayCommand]
        public void ClearSelection()
        {
            foreach (var pr in _loadedPrs)
            {
                pr.IsSelected = false;
            }
            _selectedIds.Clear();
            UpdateSelectionState();
        }

        [RelayCommand]
        public async Task DeleteSelectedPrsAsync()
        {
            if (Shell.Current == null) return;

            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;

            var label = selected.Count == 1
                ? $"{selected[0].PrNo} ({selected[0].Description})"
                : $"{selected.Count} requisitions";

            var confirm = await Shell.Current.DisplayAlertAsync(
                "Delete Requisitions",
                $"Delete {label} and all associated RFQs, PCRs, and POs? This cannot be undone.",
                "Delete",
                "Cancel");

            if (!confirm) return;

            try
            {
                foreach (var pr in selected)
                {
                    await _prRepo.DeleteAsync(pr.Id);
                    pr.PropertyChanged -= OnPrItemPropertyChanged;
                    _selectedIds.Remove(pr.Id);
                }
                ApplyFilters(resetToTop: true);
                UpdateSelectionState();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        /// <summary>Records a checkbox change. The id set is the record that outlives the loaded window;
        /// PurchaseRequisition.IsSelected is the binding, reapplied by MergeLoadedPrs on every page.</summary>
        public void SetSelected(PurchaseRequisition pr, bool isSelected)
        {
            // A card being removed from the board tears its bindings down, and a CheckBox can raise
            // CheckedChanged(false) on the way out. That is not the user unticking anything, and acting
            // on it would silently drop the selection every time the window scrolled or refiltered - so
            // only the cards actually on the board are allowed to speak for the selection.
            if (!FilteredPrs.Contains(pr)) return;

            pr.IsSelected = isSelected;
            if (isSelected) _selectedIds.Add(pr.Id);
            else _selectedIds.Remove(pr.Id);
            UpdateSelectionState();
        }

        public void UpdateSelectionState()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            SelectedPrsCount = selected.Count;
            IsBatchSelectionActive = selected.Count > 0;
            SelectedPrsSummary = selected.Count == 1 ? "1 requisition selected" : $"{selected.Count} requisitions selected";

            // 1. First button: Combine / Merge PRs vs Split / Unmerge PR
            if (selected.Count == 1 && selected[0].IsConsolidatedMaster)
            {
                MergeButtonText = "Split / Unmerge PR";
                MergeButtonToolTip = $"Unmerge {selected[0].PrNo} back into original requisitions ({selected[0].ConsolidatedFrom})";
                IsMergeOrSplitPrEnabled = true;
                CanMergeSelectedPrs = false;
            }
            else if (selected.Count >= 2 && !selected.Any(p => p.IsConsolidatedMaster))
            {
                MergeButtonText = "Combine / Merge PRs";
                MergeButtonToolTip = "Merge selected requisitions into a single combined PR";
                IsMergeOrSplitPrEnabled = true;
                CanMergeSelectedPrs = true;
            }
            else
            {
                MergeButtonText = "Combine / Merge PRs";
                MergeButtonToolTip = "Select at least 2 requisitions to combine";
                IsMergeOrSplitPrEnabled = false;
                CanMergeSelectedPrs = false;
            }

            // 2. Second button: Shared RFQ vs Split Shared RFQ
            if (selected.Count >= 1 && selected.All(p => p.Rfqs.Any(r => r.IsSharedRfq)))
            {
                SharedRfqButtonText = "Split Shared RFQ";
                SharedRfqButtonToolTip = "Unlink shared RFQ bundle into independent RFQs";
                IsSharedRfqActionEnabled = true;
            }
            else
            {
                SharedRfqButtonText = "Shared RFQ";
                SharedRfqButtonToolTip = "Create a shared RFQ for all selected requisitions";
                IsSharedRfqActionEnabled = selected.Count >= 1;
            }

            // 3. Third button: Combined PO vs Split Combined PO
            if (selected.Count >= 1 && selected.All(p => p.Pos.Any(po => po.IsCombinedPo)))
            {
                CombinedPoButtonText = "Split Combined PO";
                CombinedPoButtonToolTip = "Unlink combined PO into independent purchase orders";
                IsCombinedPoActionEnabled = true;
            }
            else
            {
                CombinedPoButtonText = "Combined PO";
                CombinedPoButtonToolTip = "Create a combined purchase order across all selected requisitions";
                IsCombinedPoActionEnabled = selected.Count >= 1;
            }
        }

        private void OnPrItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PurchaseRequisition.IsSelected))
            {
                UpdateSelectionState();
            }
        }

        // ================= PR CONSOLIDATION / MERGE OPERATIONS =================

        [RelayCommand]
        public async Task OpenMergePrModalAsync()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count < 2)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Combine Requisitions", "Please select at least 2 requisitions to combine.", "OK");
                return;
            }

            SelectedPrsForMerge = new ObservableCollection<PurchaseRequisition>(selected);

            var cleanNumbers = selected.Select(p =>
            {
                var no = p.PrNo.Trim();
                if (no.StartsWith("PR-", StringComparison.OrdinalIgnoreCase))
                    return no.Substring(3).Trim();
                if (no.StartsWith("PR ", StringComparison.OrdinalIgnoreCase))
                    return no.Substring(3).Trim();
                return no;
            }).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            // Joined from the source numbers when there are any; otherwise blank for you to fill in.
            MergeMasterPrNo = cleanNumbers.Count > 0 ? string.Join("/", cleanNumbers) : string.Empty;
            MergeDescription = string.Join("\n", selected.Select(p => $"• {p.PrNo}: {p.Description} ({p.Requestor})"));

            var requestors = selected.Select(p => p.Requestor).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
            MergeRequestor = requestors.Count == 1 ? requestors[0] : (requestors.Count > 1 ? string.Join(", ", requestors) : "Consolidated");

            MergePriority = selected.Any(p => p.IsUrgent) ? ProcurementPriority.Urgent : ProcurementPriority.Normal;
            MergeNotes = $"Consolidated from: {string.Join(", ", selected.Select(p => p.PrNo))}";
            MergeCopyChildRfqs = true;

            IsMergePrModalVisible = true;
        }

        [RelayCommand]
        public void CloseMergePrModal()
        {
            IsMergePrModalVisible = false;
            SelectedPrsForMerge.Clear();
        }

        [RelayCommand]
        public async Task ConfirmMergePrModalAsync()
        {
            if (string.IsNullOrWhiteSpace(MergeMasterPrNo))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Master PR Number is required.", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(MergeDescription))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Description is required.", "OK");
                return;
            }

            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count < 2) return;

            try
            {
                IsBusy = true;
                var masterPr = new PurchaseRequisition
                {
                    Id = Guid.NewGuid(),
                    PrNo = MergeMasterPrNo.Trim(),
                    Description = MergeDescription.Trim(),
                    Requestor = MergeRequestor.Trim(),
                    // Inherit from the sources (most common value): defaulting silently relabeled
                    // e.g. an all-NO01/Capex merge as RW01/Stores&Spares.
                    Plant = selected.GroupBy(p => p.Plant).OrderByDescending(g => g.Count()).First().Key,
                    PrType = selected.GroupBy(p => p.PrType).OrderByDescending(g => g.Count()).First().Key,
                    Priority = MergePriority,
                    Status = ProcurementStatus.PrRaised,
                    Notes = MergeNotes.Trim(),
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    ConsolidatedFrom = string.Join(", ", selected.Select(p => p.PrNo))
                };

                await _prRepo.MergePrsAsync(selected, masterPr, MergeCopyChildRfqs);

                // The master is in SQLite now; ApplyFilters below brings it into the window.
                foreach (var src in selected)
                {
                    src.IsSelected = false;
                    _selectedIds.Remove(src.Id);
                }

                IsMergePrModalVisible = false;
                SelectedPrsForMerge.Clear();
                UpdateSelectionState();
                ApplyFilters();

                ShowToast($"Combined {selected.Count} requisitions into {masterPr.PrNo}");
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

        // ================= DYNAMIC TOOLBAR ACTION ROUTING =================

        [RelayCommand]
        public async Task MergeOrSplitPrAsync()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 1 && selected[0].IsConsolidatedMaster)
            {
                await SplitMasterPrAsync(selected[0]);
            }
            else if (selected.Count >= 2)
            {
                await OpenMergePrModalAsync();
            }
        }

        [RelayCommand]
        public async Task CreateOrSplitSharedRfqAsync()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count >= 1 && selected.All(p => p.Rfqs.Any(r => r.IsSharedRfq)))
            {
                await SplitSelectedSharedRfqAsync();
            }
            else if (selected.Count >= 1)
            {
                await OpenBatchRfqModalAsync();
            }
        }

        [RelayCommand]
        public async Task CreateOrSplitCombinedPoAsync()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count >= 1 && selected.All(p => p.Pos.Any(po => po.IsCombinedPo)))
            {
                await SplitSelectedCombinedPoAsync();
            }
            else if (selected.Count >= 1)
            {
                await OpenBatchPoModalAsync();
            }
        }

        [RelayCommand]
        public async Task SplitMasterPrAsync(PurchaseRequisition masterPr)
        {
            if (masterPr == null) return;
            await OpenSplitPrModalAsync(masterPr);
        }

        public async Task OpenSplitPrModalAsync(PurchaseRequisition masterPr)
        {
            SplitTargetMasterPr = masterPr;

            // Linked by ParentPrId, or matched by the numbers listed in ConsolidatedFrom. Both are
            // asked at once now, in SQL: merged children carry status 'Merged', which the board hides,
            // so they are not in the loaded window to search.
            var prNos = string.IsNullOrWhiteSpace(masterPr.ConsolidatedFrom)
                ? new List<string>()
                : masterPr.ConsolidatedFrom.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(s => s.Trim().ToLowerInvariant())
                                           .Where(s => !string.IsNullOrWhiteSpace(s))
                                           .ToList();

            var childPrs = await Task.Run(() => _prRepo.GetChildPrsAsync(masterPr.Id, prNos));

            if (childPrs.Count == 0)
            {
                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync("Split Requisition", "Could not locate source requisitions for this combined PR.", "OK");
                }
                return;
            }

            foreach (var entry in SplitPrEntries)
                entry.PropertyChanged -= OnSplitPrEntryPropertyChanged;

            SplitPrEntries.Clear();
            foreach (var cp in childPrs)
            {
                var entry = new SplitPrEntry
                {
                    Pr = cp,
                    IsKeepCombined = true
                };
                entry.PropertyChanged += OnSplitPrEntryPropertyChanged;
                SplitPrEntries.Add(entry);
            }

            UpdateSplitActionSummary();
            IsSplitPrModalVisible = true;
        }

        private void OnSplitPrEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SplitPrEntry.IsKeepCombined))
            {
                UpdateSplitActionSummary();
            }
        }

        private void UpdateSplitActionSummary()
        {
            var keptCount = SplitPrEntries.Count(e => e.IsKeepCombined);
            var splitCount = SplitPrEntries.Count(e => !e.IsKeepCombined);
            var totalCount = SplitPrEntries.Count;

            if (splitCount == 0)
            {
                SplitActionSummary = $"All {totalCount} requisitions will remain combined. (Uncheck to separate)";
                CanConfirmSplit = false;
            }
            else if (keptCount >= 2)
            {
                SplitActionSummary = $"{splitCount} requisition(s) will be separated. Remaining {keptCount} requisitions will stay combined in {SplitTargetMasterPr?.PrNo}.";
                CanConfirmSplit = true;
            }
            else
            {
                SplitActionSummary = $"All {totalCount} requisitions will be separated back to standalone active PRs. Master PR {SplitTargetMasterPr?.PrNo} will be removed.{BuildSplitMasterDocWarning()}";
                CanConfirmSplit = true;
            }
        }

        // A full split hard-deletes every document attached to the master; without naming them the
        // confirmation read as harmless while a real raised PO was about to be destroyed.
        private string BuildSplitMasterDocWarning()
        {
            var master = SplitTargetMasterPr;
            if (master == null) return string.Empty;

            var parts = new List<string>();
            if (master.Pos != null && master.Pos.Count > 0)
                parts.Add($"{master.Pos.Count} raised PO(s) ({string.Join(", ", master.Pos.Select(p => p.PoNo))})");
            if (master.Rfqs != null && master.Rfqs.Count > 0)
                parts.Add($"{master.Rfqs.Count} RFQ(s)");
            if (master.Pcr != null)
                parts.Add("the PCR record");

            return parts.Count == 0
                ? string.Empty
                : $" WARNING: {string.Join(", ", parts)} recorded on the master will be permanently deleted.";
        }

        [RelayCommand]
        public void SelectAllKeepCombined()
        {
            foreach (var entry in SplitPrEntries)
                entry.IsKeepCombined = true;
            UpdateSplitActionSummary();
        }

        [RelayCommand]
        public void SelectNoneKeepCombined()
        {
            foreach (var entry in SplitPrEntries)
                entry.IsKeepCombined = false;
            UpdateSplitActionSummary();
        }

        [RelayCommand]
        public void CloseSplitPrModal()
        {
            foreach (var entry in SplitPrEntries)
                entry.PropertyChanged -= OnSplitPrEntryPropertyChanged;

            SplitPrEntries.Clear();
            SplitTargetMasterPr = null;
            IsSplitPrModalVisible = false;
        }

        [RelayCommand]
        public async Task ConfirmSplitPrModalAsync()
        {
            if (SplitTargetMasterPr == null) return;

            var kept = SplitPrEntries.Where(e => e.IsKeepCombined).Select(e => e.Pr).ToList();
            var split = SplitPrEntries.Where(e => !e.IsKeepCombined).Select(e => e.Pr).ToList();

            if (split.Count == 0)
            {
                IsSplitPrModalVisible = false;
                return;
            }

            try
            {
                IsBusy = true;
                var targetPrNo = SplitTargetMasterPr.PrNo;
                if (kept.Count < 2)
                {
                    await _prRepo.SplitMergedPrAsync(SplitTargetMasterPr.Id);
                }
                else
                {
                    await _prRepo.PartialSplitMergedPrAsync(SplitTargetMasterPr.Id, split, kept);
                }

                CloseSplitPrModal();
                UpdateSelectionState();
                ApplyFilters();

                if (Shell.Current != null)
                {
                    if (kept.Count < 2)
                    {
                        await Shell.Current.DisplayAlertAsync(
                            "Requisitions Restored",
                            $"Successfully split {targetPrNo}. All source requisitions are now active on your board.",
                            "OK");
                    }
                    else
                    {
                        await Shell.Current.DisplayAlertAsync(
                            "Requisitions Split",
                            $"Successfully separated {split.Count} requisition(s) ({string.Join(", ", split.Select(s => s.PrNo))}). Remaining {kept.Count} requisitions remain combined in master requisition.",
                            "OK");
                    }
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

        [RelayCommand]
        public async Task SplitSelectedSharedRfqAsync()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            var sharedRfqs = selected.SelectMany(p => p.Rfqs.Where(r => r.IsSharedRfq)).ToList();
            if (sharedRfqs.Count == 0) return;

            var firstRfq = sharedRfqs[0];
            await SplitSharedRfqAsync(firstRfq);
        }

        [RelayCommand]
        public async Task SplitSharedRfqAsync(RequestForQuotation rfq)
        {
            if (rfq == null || Shell.Current == null) return;

            var confirm = await Shell.Current.DisplayAlertAsync(
                "Split Shared RFQ",
                $"Unlink shared RFQ {rfq.RfqNo}? Each linked requisition will keep its own independent quotation.",
                "Split",
                "Cancel");

            if (!confirm) return;

            try
            {
                IsBusy = true;
                await _prRepo.SplitSharedRfqAsync(rfq.Id);

                // Clear in-memory SharedPrs across matching RFQs
                var targetRfqNo = rfq.RfqNo;
                foreach (var pr in _loadedPrs)
                {
                    foreach (var r in pr.Rfqs)
                    {
                        if (r.Id == rfq.Id || (!string.IsNullOrEmpty(targetRfqNo) && r.RfqNo == targetRfqNo))
                        {
                            r.SharedPrs = string.Empty;
                            r.NotifyCalculationsChanged();
                        }
                    }
                    pr.NotifyHierarchyChanged();
                }

                UpdateSelectionState();
                ApplyFilters();

                await Shell.Current.DisplayAlertAsync("Shared RFQ Unlinked", $"Shared RFQ {rfq.RfqNo} has been unlinked into independent quotations.", "OK");
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

        [RelayCommand]
        public async Task SplitSelectedCombinedPoAsync()
        {
            var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
            var combinedPos = selected.SelectMany(p => p.Pos.Where(po => po.IsCombinedPo)).ToList();
            if (combinedPos.Count == 0) return;

            var firstPo = combinedPos[0];
            await SplitCombinedPoAsync(firstPo);
        }

        [RelayCommand]
        public async Task SplitCombinedPoAsync(PurchaseOrder po)
        {
            if (po == null || Shell.Current == null) return;

            var confirm = await Shell.Current.DisplayAlertAsync(
                "Split Combined PO",
                $"Unlink combined purchase order {po.PoNo}? Each linked requisition will maintain its independent PO.",
                "Split",
                "Cancel");

            if (!confirm) return;

            try
            {
                IsBusy = true;
                await _prRepo.SplitCombinedPoAsync(po.Id);

                var targetPoNo = po.PoNo;
                foreach (var pr in _loadedPrs)
                {
                    foreach (var p in pr.Pos)
                    {
                        if (p.Id == po.Id || (!string.IsNullOrEmpty(targetPoNo) && p.PoNo == targetPoNo))
                        {
                            p.CombinedPrs = string.Empty;
                        }
                    }
                    pr.NotifyHierarchyChanged();
                }

                UpdateSelectionState();
                ApplyFilters();

                await Shell.Current.DisplayAlertAsync("Combined PO Unlinked", $"Purchase order {po.PoNo} has been unlinked into independent orders.", "OK");
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
