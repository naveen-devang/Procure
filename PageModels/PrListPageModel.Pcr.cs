using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    // Price comparison requests: approvals, the stages config modal, and the CSV /
    // Excel / PDF exports.
    public partial class PrListPageModel
    {
        // PCR Approval stages config modal state
        [ObservableProperty]
        public partial bool IsApprovalConfigModalVisible { get; set; }
        [ObservableProperty]
        public partial PurchaseRequisition? ConfiguringPr { get; set; }
        [ObservableProperty]
        public partial ObservableCollection<Approval> ConfiguringApprovals { get; set; } = new();
        [ObservableProperty]
        public partial string NewStageRoleName { get; set; } = string.Empty;
        [ObservableProperty]
        public partial bool NewStageRequiresMultipleDates { get; set; } = true;


        // ================= PCR & APPROVAL OPERATIONS =================

        [RelayCommand]
        public async Task CreatePcrForPrAsync(PurchaseRequisition pr)
        {
            try
            {
                var pcr = new PriceComparisonRequest
                {
                    Id = Guid.NewGuid(),
                    PrId = pr.Id,
                    PcrNo = $"PCR-{pr.PrNo.Replace("PR-", "")}",
                    CreatedAt = DateTime.Now
                };
                var defaultRoles = _settingsService.GetDefaultApprovalRoles();
                pcr.EnsureDefaultApprovals(defaultRoles);

                await _prRepo.SavePcrAsync(pcr);
                pr.Pcr = pcr;
                pr.Status = ProcurementStatus.PcrSubmitted;
                await _prRepo.SavePrFieldsAsync(pr);
                pr.NotifyHierarchyChanged();
                UpdateStatusBanner();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task SetApprovalSentTodayAsync(Approval approval)
        {
            try
            {
                approval.SentDate = DateTime.Today;
                approval.NotifyPropertiesChanged();
                await _prRepo.UpdateApprovalAsync(approval);
                UpdateParentPrApprovalState(approval);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task SetApprovalReceivedTodayAsync(Approval approval)
        {
            try
            {
                approval.ReceivedDate = DateTime.Today;
                approval.Signed = true;
                approval.SignedDate = DateTime.Today;
                approval.NotifyPropertiesChanged();
                await _prRepo.UpdateApprovalAsync(approval);
                UpdateParentPrApprovalState(approval);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task ClearSentDateAsync(Approval approval)
        {
            try
            {
                approval.SentDate = null;
                approval.NotifyPropertiesChanged();
                await _prRepo.UpdateApprovalAsync(approval);
                UpdateParentPrApprovalState(approval);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task ClearReceivedDateAsync(Approval approval)
        {
            try
            {
                approval.ReceivedDate = null;
                approval.Signed = false;
                approval.SignedDate = null;
                approval.NotifyPropertiesChanged();
                await _prRepo.UpdateApprovalAsync(approval);
                UpdateParentPrApprovalState(approval);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public async Task HandleApprovalDateChangedAsync(Approval approval)
        {
            try
            {
                approval.NotifyPropertiesChanged();
                await _prRepo.UpdateApprovalAsync(approval);
                UpdateParentPrApprovalState(approval);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ================= PCR STAGES CONFIG MODAL =================

        [RelayCommand]
        public async Task OpenApprovalConfigModalAsync(PurchaseRequisition pr)
        {
            try
            {
                if (pr.Pcr == null)
                {
                    await CreatePcrForPrAsync(pr);
                }

                ConfiguringPr = pr;
                ConfiguringApprovals.Clear();
                if (pr.Pcr != null)
                {
                    foreach (var a in pr.Pcr.Approvals)
                    {
                        ConfiguringApprovals.Add(a.Clone());
                    }
                }

                NewStageRoleName = string.Empty;
                NewStageRequiresMultipleDates = true;
                IsApprovalConfigModalVisible = true;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void CloseApprovalConfigModal()
        {
            IsApprovalConfigModalVisible = false;
            ConfiguringPr = null;
            ConfiguringApprovals.Clear();
        }

        [RelayCommand]
        public void AddNewStageToConfig()
        {
            if (string.IsNullOrWhiteSpace(NewStageRoleName)) return;

            var newApproval = new Approval
            {
                Id = Guid.NewGuid(),
                PcrId = ConfiguringPr?.Pcr?.Id ?? Guid.NewGuid(),
                Role = NewStageRoleName.Trim(),
                RequiresMultipleDates = NewStageRequiresMultipleDates,
                SortOrder = ConfiguringApprovals.Count
            };

            ConfiguringApprovals.Add(newApproval);
            NewStageRoleName = string.Empty;
            NewStageRequiresMultipleDates = true;
        }

        [RelayCommand]
        public void RemoveStageFromConfig(Approval approval)
        {
            ConfiguringApprovals.Remove(approval);
            for (int i = 0; i < ConfiguringApprovals.Count; i++)
            {
                ConfiguringApprovals[i].SortOrder = i;
            }
        }

        [RelayCommand]
        public void ToggleApprovalInclusion(Approval approval)
        {
            if (approval != null)
            {
                approval.IsIncluded = !approval.IsIncluded;
            }
        }

        public void ReorderApprovalStages(Approval source, Approval target)
        {
            int oldIndex = ConfiguringApprovals.IndexOf(source);
            int newIndex = ConfiguringApprovals.IndexOf(target);
            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                ConfiguringApprovals.Move(oldIndex, newIndex);
                for (int i = 0; i < ConfiguringApprovals.Count; i++)
                {
                    ConfiguringApprovals[i].SortOrder = i;
                }
            }
        }

        [RelayCommand]
        public async Task SaveApprovalConfigModalAsync()
        {
            if (ConfiguringPr?.Pcr == null)
            {
                CloseApprovalConfigModal();
                return;
            }

            try
            {
                var pcr = ConfiguringPr.Pcr;
                pcr.Approvals.Clear();
                int order = 0;
                foreach (var a in ConfiguringApprovals.Where(x => x.IsIncluded))
                {
                    a.PcrId = pcr.Id;
                    a.SortOrder = order++;
                    pcr.Approvals.Add(a);
                }

                await _prRepo.SavePcrAsync(pcr);

                if (pcr.IsFullyApproved)
                {
                    ConfiguringPr.Status = ProcurementStatus.PcrApproved;
                    await _prRepo.SavePrFieldsAsync(ConfiguringPr);
                }
                else if (ConfiguringPr.Status == ProcurementStatus.PcrApproved)
                {
                    ConfiguringPr.Status = ProcurementStatus.PcrSubmitted;
                    await _prRepo.SavePrFieldsAsync(ConfiguringPr);
                }

                ConfiguringPr.NotifyHierarchyChanged();
                UpdateStatusBanner();
                CloseApprovalConfigModal();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        private async void UpdateParentPrApprovalState(Approval approval)
        {
            try
            {
                var parentPr = _loadedPrs.FirstOrDefault(p => p.Pcr != null && p.Pcr.Approvals.Any(a => a.Id == approval.Id))
                               ?? FilteredPrs.FirstOrDefault(p => p.Pcr != null && p.Pcr.Approvals.Any(a => a.Id == approval.Id));
                if (parentPr?.Pcr != null)
                {
                    if (parentPr.Pcr.IsFullyApproved)
                    {
                        parentPr.Status = ProcurementStatus.PcrApproved;
                        await _prRepo.SavePrFieldsAsync(parentPr);
                    }
                    else if (parentPr.Status == ProcurementStatus.PcrApproved)
                    {
                        parentPr.Status = ProcurementStatus.PcrSubmitted;
                        await _prRepo.SavePrFieldsAsync(parentPr);
                    }

                    parentPr.Pcr.NotifyApprovalsChanged();
                    parentPr.NotifyHierarchyChanged();
                    UpdateStatusBanner();
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }


        // ================= CSV EXPORT =================

        // The export is the one command that still reads the whole table, so it is the one command that
        // can take seconds on a large database - roughly 3s at 20,000 PRs, where it used to be instant
        // off the in-memory list. Disallowing concurrent runs makes the button disable itself while it
        // works, so the wait reads as "working" rather than "nothing happened, click it again".
        [RelayCommand(AllowConcurrentExecutions = false)]
        public async Task ExportCsvAsync()
        {
            try
            {
                // The one place that still wants every PR. Read, build and write on a background
                // thread - the string build alone is most of the ~3s at 20,000 PRs and used to run
                // on the dispatcher.
                var filePath = await Task.Run(async () =>
                {
                    var all = await _prRepo.GetAllAsync();
                    var csv = await _csvExportService.ExportPrsToCsvAsync(all, CustomColumnDefinitions);
                    return await _csvExportService.SaveExportToFileAsync(csv);
                });

                // Same behavior as the PCR exports: hand the file to the OS viewer instead of
                // dead-ending at a path in a dialog.
                try
                {
                    await Launcher.Default.OpenAsync(new OpenFileRequest
                    {
                        Title = Path.GetFileName(filePath),
                        File = new ReadOnlyFile(filePath)
                    });
                }
                catch
                {
                    // Non-fatal if no viewer is available; the toast still names the file.
                }

                await Toast.Make($"Exported {Path.GetFileName(filePath)}").Show();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ================= PRICE COMPARISON EXPORT (EXCEL & PDF) =================

        [ObservableProperty]
        public partial bool IsExportPcrModalVisible { get; set; }

        [ObservableProperty]
        public partial PurchaseRequisition? ExportTargetPr { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<ExportRfqSelection> ExportRfqSelections { get; set; } = new();

        [ObservableProperty]
        public partial string ExportPcrRemarks { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ExportPcrSubtitle { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SelectedRfqCountMessage { get; set; } = string.Empty;

        [RelayCommand]
        public void OpenExportPcrModal(PurchaseRequisition pr)
        {
            if (pr == null) return;
            ExportTargetPr = pr;
            var plant = string.IsNullOrWhiteSpace(pr.Plant) ? "RW01" : pr.Plant.Trim();
            ExportPcrSubtitle = $"Requisition {pr.PrNo} — {plant}";

            if (pr.Pcr != null && !string.IsNullOrWhiteSpace(pr.Pcr.Remarks))
            {
                ExportPcrRemarks = pr.Pcr.Remarks;
            }
            else if (!string.IsNullOrWhiteSpace(pr.Notes))
            {
                ExportPcrRemarks = pr.Notes;
            }
            else
            {
                ExportPcrRemarks = string.Empty;
            }

            foreach (var sel in ExportRfqSelections)
            {
                sel.PropertyChanged -= OnExportRfqSelectionPropertyChanged;
            }
            ExportRfqSelections.Clear();

            if (pr.Rfqs != null && pr.Rfqs.Count > 0)
            {
                int initialCount = 0;
                foreach (var rfq in pr.Rfqs)
                {
                    bool isSel = initialCount < 5;
                    if (isSel) initialCount++;
                    var sel = new ExportRfqSelection(rfq, isSel);
                    sel.PropertyChanged += OnExportRfqSelectionPropertyChanged;
                    ExportRfqSelections.Add(sel);
                }
            }

            UpdateSelectedRfqCount();
            IsExportPcrModalVisible = true;
        }

        private void OnExportRfqSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ExportRfqSelection.IsSelected))
            {
                var selectedCount = ExportRfqSelections.Count(s => s.IsSelected);
                if (selectedCount > 5 && sender is ExportRfqSelection toggled)
                {
                    toggled.PropertyChanged -= OnExportRfqSelectionPropertyChanged;
                    toggled.IsSelected = false;
                    toggled.PropertyChanged += OnExportRfqSelectionPropertyChanged;

                    if (Shell.Current != null)
                    {
                        _ = Shell.Current.DisplayAlertAsync("Limit Reached", "A maximum of 5 suppliers can be selected on the comparison sheet.", "OK");
                    }
                }
                UpdateSelectedRfqCount();
            }
        }

        private void UpdateSelectedRfqCount()
        {
            var count = ExportRfqSelections.Count(s => s.IsSelected);
            SelectedRfqCountMessage = $"{count} of 5 suppliers selected";
        }

        [RelayCommand]
        public void CloseExportPcrModal()
        {
            foreach (var sel in ExportRfqSelections)
            {
                sel.PropertyChanged -= OnExportRfqSelectionPropertyChanged;
            }
            ExportRfqSelections.Clear();
            IsExportPcrModalVisible = false;
            ExportTargetPr = null;
        }

        [RelayCommand]
        public async Task ExportPcrExcelAsync()
        {
            if (ExportTargetPr == null) return;

            var selected = ExportRfqSelections.Where(s => s.IsSelected).Select(s => s.Rfq).ToList();
            if (selected.Count == 0)
            {
                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync("No Supplier Selected", "Please select at least 1 supplier quotation to export.", "OK");
                }
                return;
            }

            try
            {
                if (ExportTargetPr.Pcr != null)
                {
                    ExportTargetPr.Pcr.Remarks = ExportPcrRemarks;
                    await _prRepo.SavePcrAsync(ExportTargetPr.Pcr);
                }

                var pcr = ExportTargetPr.Pcr ?? new PriceComparisonRequest
                {
                    Id = Guid.NewGuid(),
                    PrId = ExportTargetPr.Id,
                    PcrNo = $"PCR-{ExportTargetPr.PrNo.Replace("PR-", "")}",
                    Remarks = ExportPcrRemarks
                };

                var filePath = await _pcrExportService.ExportPcrToExcelAsync(ExportTargetPr, pcr, selected, ExportPcrRemarks);
                CloseExportPcrModal();

                // The exporter already opened the file; the result is visible, so no blocking dialog.
                await Toast.Make($"Exported {Path.GetFileName(filePath)}").Show();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task ExportPcrPdfAsync()
        {
            if (ExportTargetPr == null) return;

            var selected = ExportRfqSelections.Where(s => s.IsSelected).Select(s => s.Rfq).ToList();
            if (selected.Count == 0)
            {
                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync("No Supplier Selected", "Please select at least 1 supplier quotation to export.", "OK");
                }
                return;
            }

            try
            {
                if (ExportTargetPr.Pcr != null)
                {
                    ExportTargetPr.Pcr.Remarks = ExportPcrRemarks;
                    await _prRepo.SavePcrAsync(ExportTargetPr.Pcr);
                }

                var pcr = ExportTargetPr.Pcr ?? new PriceComparisonRequest
                {
                    Id = Guid.NewGuid(),
                    PrId = ExportTargetPr.Id,
                    PcrNo = $"PCR-{ExportTargetPr.PrNo.Replace("PR-", "")}",
                    Remarks = ExportPcrRemarks
                };

                var filePath = await _pcrExportService.ExportPcrToPdfAsync(ExportTargetPr, pcr, selected, ExportPcrRemarks);
                CloseExportPcrModal();

                await Toast.Make($"Exported {Path.GetFileName(filePath)}").Show();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

    }
}
