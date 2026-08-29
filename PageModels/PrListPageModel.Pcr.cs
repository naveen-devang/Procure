using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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

                ShowToast($"Exported {Path.GetFileName(filePath)}");
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
                // Quote-received RFQs fill the 5 export slots first — picking purely by list
                // position let unquoted vendors (exported as blank columns) crowd out real quotes.
                var preferred = new HashSet<Guid>(pr.Rfqs
                    .OrderByDescending(r => r.IsQuoteReceived ? 1 : 0)
                    .Take(5)
                    .Select(r => r.Id));
                foreach (var rfq in pr.Rfqs)
                {
                    var sel = new ExportRfqSelection(rfq, preferred.Contains(rfq.Id));
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
            ReleasePcrPreview();
        }

        /// <summary>Drops the rendered preview's memory: one LOH-sized PNG per page, the PDF bytes,
        /// and the PR graph they were rendered from. Called from every path that dismisses the export
        /// modal, because Back-then-close reached neither ClosePcrPreview nor a regenerate and left
        /// all of it stranded on this singleton for the rest of the session.</summary>
        private void ReleasePcrPreview()
        {
            PcrPreviewPages.Clear();
            PcrPreviewCurrentPage = null;
            _pcrPreviewBytes = null;
            _pcrPreviewPr = null;
            _pcrPreviewRfqs = null;
        }

        // The modal promises "This is saved for future exports" — previously a PR without a PCR
        // record got a transient PriceComparisonRequest that was never persisted, so the typed
        // remarks (and the generated PcrNo) silently vanished after every export.
        private async Task<PriceComparisonRequest> GetOrCreateSavedPcrAsync(PurchaseRequisition pr)
        {
            if (pr.Pcr != null)
            {
                pr.Pcr.Remarks = ExportPcrRemarks;
                await _prRepo.SavePcrAsync(pr.Pcr);
                return pr.Pcr;
            }

            var pcr = new PriceComparisonRequest
            {
                Id = Guid.NewGuid(),
                PrId = pr.Id,
                PcrNo = $"PCR-{pr.PrNo.Replace("PR-", "")}",
                Remarks = ExportPcrRemarks
            };
            await _prRepo.SavePcrAsync(pcr);
            pr.Pcr = pcr;
            return pcr;
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
                var pcr = await GetOrCreateSavedPcrAsync(ExportTargetPr);

                var filePath = await _pcrExportService.ExportPcrToExcelAsync(ExportTargetPr, pcr, selected, ExportPcrRemarks);
                CloseExportPcrModal();

                // The exporter already opened the file; the result is visible, so no blocking dialog.
                ShowToast($"Exported {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ================= PDF PREVIEW (layout options, save, print) =================

        [ObservableProperty]
        public partial bool IsPcrPreviewVisible { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<ImageSource> PcrPreviewPages { get; set; } = new();

        [ObservableProperty]
        public partial bool IsPcrPreviewBusy { get; set; }

        [ObservableProperty]
        public partial string PcrPreviewPageSummary { get; set; } = string.Empty;

        // Scaling and signature boxes are plain on/off, so a CheckBox binds straight to a bool.
        // Orientation/Paper/Margins are 3-way choices — same string-property + segmented-button
        // pattern SettingsPageModel already uses for Color Mode and Accent Theme (SelectedThemeMode).
        [ObservableProperty]
        public partial bool PcrShrinkToFit { get; set; }

        [ObservableProperty]
        public partial string PcrOrientation { get; set; } = "Landscape";

        [ObservableProperty]
        public partial string PcrPaperSize { get; set; } = "A4";

        [ObservableProperty]
        public partial string PcrMarginPreset { get; set; } = "Normal";

        [ObservableProperty]
        public partial bool PcrIncludeSignatureBoxes { get; set; } = true;

        [ObservableProperty]
        public partial int PcrPreviewPageIndex { get; set; }

        [ObservableProperty]
        public partial ImageSource? PcrPreviewCurrentPage { get; set; }

        [ObservableProperty]
        public partial string PcrPreviewPagerText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsPcrPagerVisible { get; set; }

        // Printer selection lives in this modal instead of behind a separate OS dialog — populated
        // once when the preview opens; printing always targets whichever bitmaps are on screen.
        [ObservableProperty]
        public partial ObservableCollection<string> PcrAvailablePrinters { get; set; } = new();

        [ObservableProperty]
        public partial string? PcrSelectedPrinter { get; set; }

        // Always left enabled: querying a driver's actual duplex support ahead of time (via
        // PrinterSettings.CanDuplex) proved unreliable for some printers/virtual printers and left
        // the checkbox looking dead. PrintPcrPdfAsync re-checks CanDuplex right before printing and
        // falls back to one-sided silently, so nothing is lost by not gating the checkbox on it here.
        [ObservableProperty]
        public partial bool PcrDoubleSided { get; set; }

        // Adobe-style range box: blank means every page, "1-3,5" means pages 1,2,3,5.
        [ObservableProperty]
        public partial string PcrPageRangeText { get; set; } = string.Empty;

        // Word-style Copies box. Text rather than int so a half-typed or cleared field never throws
        // a binding error; parsed and clamped to 1-99 at print time.
        [ObservableProperty]
        public partial string PcrCopiesText { get; set; } = "1";

        internal static int ParseCopies(string? text)
            => int.TryParse(text?.Trim(), out var n) ? Math.Clamp(n, 1, 99) : 1;

        // Zoom is a pure Scale transform on the page image (no layout pass), and pan is drag-driven
        // (TranslationX/Y) rather than mouse-wheel-driven - see PcrPreviewModal.xaml.cs for why.
        [ObservableProperty]
        public partial double PcrPreviewZoom { get; set; } = 1.0;

        [ObservableProperty]
        public partial double PcrPreviewPanX { get; set; }

        [ObservableProperty]
        public partial double PcrPreviewPanY { get; set; }

        [ObservableProperty]
        public partial string PcrPreviewZoomLabel { get; set; } = "100%";

        partial void OnPcrPreviewZoomChanged(double value) => PcrPreviewZoomLabel = $"{value:P0}";

        private byte[]? _pcrPreviewBytes;
        private PurchaseRequisition? _pcrPreviewPr;
        private List<RequestForQuotation>? _pcrPreviewRfqs;
        private string _pcrPreviewRemarksSnapshot = string.Empty;
        private int _pcrPreviewGeneration;

        // Full ISO + US set, same list Word/Excel offer - a Picker binds straight to PcrPaperSize
        // (TwoWay), so unlike Orientation/Margins this one needs no Select*Command.
        public List<string> PcrPaperSizeOptions { get; } = new()
        {
            "A0", "A1", "A2", "A3", "A4", "A5", "A6", "Letter", "Legal", "Tabloid", "Executive"
        };

        partial void OnPcrShrinkToFitChanged(bool value) => _ = RegeneratePcrPreviewAsync();
        partial void OnPcrPaperSizeChanged(string value) => _ = RegeneratePcrPreviewAsync();
        partial void OnPcrIncludeSignatureBoxesChanged(bool value) => _ = RegeneratePcrPreviewAsync();

        [RelayCommand]
        public void SelectPcrOrientation(string orientation)
        {
            if (PcrOrientation == orientation) return;
            PcrOrientation = orientation;
            _ = RegeneratePcrPreviewAsync();
        }

        [RelayCommand]
        public void SelectPcrMarginPreset(string marginPreset)
        {
            if (PcrMarginPreset == marginPreset) return;
            PcrMarginPreset = marginPreset;
            _ = RegeneratePcrPreviewAsync();
        }

        [RelayCommand]
        public void NextPcrPreviewPage()
        {
            if (PcrPreviewPageIndex >= PcrPreviewPages.Count - 1) return;
            PcrPreviewPageIndex++;
            ShowPcrPreviewPage();
        }

        [RelayCommand]
        public void PreviousPcrPreviewPage()
        {
            if (PcrPreviewPageIndex <= 0) return;
            PcrPreviewPageIndex--;
            ShowPcrPreviewPage();
        }

        /// <summary>Points the visible image at <see cref="PcrPreviewPageIndex"/> and resets zoom —
        /// a page switch showing whatever zoom the previous page was left at reads as a bug, not a
        /// feature, since the new page is a different sheet.</summary>
        private void ShowPcrPreviewPage()
        {
            PcrPreviewCurrentPage = PcrPreviewPageIndex < PcrPreviewPages.Count
                ? PcrPreviewPages[PcrPreviewPageIndex]
                : null;
            PcrPreviewPagerText = $"Page {PcrPreviewPageIndex + 1} of {PcrPreviewPages.Count}";
            PcrPreviewZoom = 1.0;
            PcrPreviewPanX = 0;
            PcrPreviewPanY = 0;
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
                var pcr = await GetOrCreateSavedPcrAsync(ExportTargetPr);

                _pcrPreviewPr = ExportTargetPr;
                _pcrPreviewRfqs = selected;
                _pcrPreviewRemarksSnapshot = ExportPcrRemarks;
                ExportTargetPr.Pcr = pcr;

                IsExportPcrModalVisible = false;
                IsPcrPreviewVisible = true;

                PcrDoubleSided = false;
                PcrPageRangeText = string.Empty;
                PcrCopiesText = "1";

                var printers = await Task.Run(() => _pcrExportService.GetAvailablePrinters());
                PcrAvailablePrinters = new ObservableCollection<string>(printers);
                var defaultPrinter = await Task.Run(() => _pcrExportService.GetDefaultPrinterName());
                PcrSelectedPrinter = printers.Contains(defaultPrinter) ? defaultPrinter : printers.FirstOrDefault();

                await RegeneratePcrPreviewAsync();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        /// <summary>Regenerates the previewed PDF whenever a layout option changes. Guarded by a
        /// generation counter — same pattern as the board's page loads — so a quick double-toggle
        /// doesn't leave a stale render overwriting a newer one.</summary>
        private async Task RegeneratePcrPreviewAsync()
        {
            if (_pcrPreviewPr?.Pcr == null || _pcrPreviewRfqs == null) return;

            var generation = ++_pcrPreviewGeneration;
            IsPcrPreviewBusy = true;
            try
            {
                var options = BuildPcrPdfOptions();

                var pr = _pcrPreviewPr;
                var pcr = pr.Pcr!;
                var rfqs = _pcrPreviewRfqs;
                var remarks = _pcrPreviewRemarksSnapshot;

                var (bytes, images) = await Task.Run(async () =>
                {
                    var pdfBytes = _pcrExportService.GeneratePcrPdfBytes(pr, pcr, rfqs, remarks, options);
                    var pages = (await PcrPdfRasterizer.RenderPagesAsync(pdfBytes)).Pages;
                    return (pdfBytes, pages);
                });

                if (generation != _pcrPreviewGeneration) return;

                _pcrPreviewBytes = bytes;
                PcrPreviewPages.Clear();
                foreach (var png in images)
                {
                    PcrPreviewPages.Add(ImageSource.FromStream(() => new MemoryStream(png)));
                }
                PcrPreviewPageSummary = images.Count == 1 ? "1 page" : $"{images.Count} pages";
                IsPcrPagerVisible = images.Count > 1;
                PcrPreviewPageIndex = 0;
                ShowPcrPreviewPage();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                if (generation == _pcrPreviewGeneration) IsPcrPreviewBusy = false;
            }
        }

        /// <summary>The layout options currently shown in the preview panel, as the exporter wants
        /// them. Shared by the preview render and the print path, which adds a page subset on top.</summary>
        private PcrPdfOptions BuildPcrPdfOptions() => new()
        {
            LayoutMode = PcrShrinkToFit ? PdfLayoutMode.ShrinkToFit : PdfLayoutMode.AsGenerated,
            PaperSize = PcrPaperSize switch
            {
                "A0" => PdfPaperSize.A0,
                "A1" => PdfPaperSize.A1,
                "A2" => PdfPaperSize.A2,
                "A3" => PdfPaperSize.A3,
                "A5" => PdfPaperSize.A5,
                "A6" => PdfPaperSize.A6,
                "Letter" => PdfPaperSize.Letter,
                "Legal" => PdfPaperSize.Legal,
                "Tabloid" => PdfPaperSize.Tabloid,
                "Executive" => PdfPaperSize.Executive,
                _ => PdfPaperSize.A4
            },
            Orientation = PcrOrientation == "Portrait" ? PdfOrientation.Portrait : PdfOrientation.Landscape,
            MarginPreset = PcrMarginPreset switch { "Narrow" => PdfMarginPreset.Narrow, "Wide" => PdfMarginPreset.Wide, _ => PdfMarginPreset.Normal },
            IncludeSignatureBoxes = PcrIncludeSignatureBoxes
        };

        /// <summary>Returns to the supplier-selection modal without losing the rendered preview's
        /// source selection — suppliers/remarks are untouched, so reopening regenerates instantly.</summary>
        [RelayCommand]
        public void BackToPcrExportModal()
        {
            IsPcrPreviewVisible = false;
            IsExportPcrModalVisible = true;
        }

        [RelayCommand]
        public void ClosePcrPreview()
        {
            IsPcrPreviewVisible = false;
            PcrPreviewPageIndex = 0;
            PcrPreviewZoom = 1.0;
            PcrPreviewPanX = 0;
            PcrPreviewPanY = 0;
            IsPcrPagerVisible = false;
            PcrDoubleSided = false;
            PcrPageRangeText = string.Empty;
            PcrShrinkToFit = false;
            PcrOrientation = "Landscape";
            PcrPaperSize = "A4";
            PcrMarginPreset = "Normal";
            PcrIncludeSignatureBoxes = true;
            CloseExportPcrModal();
        }

        [RelayCommand]
        public async Task SavePcrPreviewAsync()
        {
            if (_pcrPreviewBytes == null || _pcrPreviewPr == null) return;

            try
            {
                var safePrNo = string.IsNullOrWhiteSpace(_pcrPreviewPr.PrNo) ? "PR" : _pcrPreviewPr.PrNo.Replace("/", "-").Replace("\\", "-");
                var suggested = $"PriceComparison_{safePrNo}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";

                var savedPath = await _pcrExportService.SavePcrPdfAsync(_pcrPreviewBytes, suggested);
                if (savedPath == null)
                {
                    ShowToast("Save cancelled");
                    return;
                }

                var fileName = Path.GetFileName(savedPath);
                ClosePcrPreview();
                ShowToast($"Exported {fileName}");
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task PrintPcrPreviewAsync()
        {
            if (_pcrPreviewBytes == null || _pcrPreviewPr == null) return;
            if (string.IsNullOrWhiteSpace(PcrSelectedPrinter))
            {
                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync("No Printer Selected", "Choose a printer from the list first.", "OK");
                }
                return;
            }

            if (!TryParsePcrPageRange(PcrPageRangeText, PcrPreviewPages.Count, out var pageIndices, out var rangeError))
            {
                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync("Invalid Page Range", rangeError, "OK");
                }
                return;
            }

            try
            {
                // A PDF/XPS-writer "printer" saves a file, so there is no printer to apply the page
                // range - cut the PDF itself down to it instead, the way Acrobat's own print-to-PDF
                // does, keeping the original "Page N of M" labels. A full range is the bytes on screen.
                var pdfBytes = _pcrPreviewBytes;
                var isSubset = pageIndices.Count < PcrPreviewPages.Count;
                if (isSubset && _pcrExportService.IsFileWriterPrinter(PcrSelectedPrinter) && _pcrPreviewRfqs != null)
                {
                    var pr = _pcrPreviewPr;
                    var pcr = pr.Pcr!;
                    var rfqs = _pcrPreviewRfqs;
                    var remarks = _pcrPreviewRemarksSnapshot;
                    var options = BuildPcrPdfOptions() with { PagesToEmit = pageIndices };
                    pdfBytes = await Task.Run(() => _pcrExportService.GeneratePcrPdfBytes(pr, pcr, rfqs, remarks, options));
                }

                var copies = ParseCopies(PcrCopiesText);
                var succeeded = await _pcrExportService.PrintPcrPdfAsync(pdfBytes, PcrSelectedPrinter, $"Price Comparison - {_pcrPreviewPr.PrNo}", PcrDoubleSided, pageIndices, copies);
                ShowToast(succeeded ? $"Sent to {PcrSelectedPrinter}" : "Print cancelled");
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        /// <summary>Adobe-Acrobat-style page range: blank means every page; otherwise a comma
        /// separated list of page numbers and/or "N-M" ranges, 1-based and inclusive. Returns
        /// 0-based indices, de-duplicated and sorted, ready to hand to the printer.</summary>
        private static bool TryParsePcrPageRange(string input, int totalPages, out List<int> zeroBasedIndices, out string error)
        {
            zeroBasedIndices = new List<int>();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(input))
            {
                zeroBasedIndices = Enumerable.Range(0, totalPages).ToList();
                return true;
            }

            var pages = new SortedSet<int>();
            foreach (var rawPart in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = rawPart.Trim();
                if (part.Contains('-'))
                {
                    var bounds = part.Split('-', 2);
                    if (bounds.Length != 2
                        || !int.TryParse(bounds[0].Trim(), out var from)
                        || !int.TryParse(bounds[1].Trim(), out var to)
                        || from < 1 || to < from || to > totalPages)
                    {
                        error = $"\"{part}\" isn't a valid range for this {totalPages}-page document.";
                        return false;
                    }
                    for (var p = from; p <= to; p++) pages.Add(p - 1);
                }
                else
                {
                    if (!int.TryParse(part, out var page) || page < 1 || page > totalPages)
                    {
                        error = $"\"{part}\" isn't a valid page number for this {totalPages}-page document.";
                        return false;
                    }
                    pages.Add(page - 1);
                }
            }

            if (pages.Count == 0)
            {
                error = "Enter at least one page, e.g. \"1-3, 5\".";
                return false;
            }

            zeroBasedIndices = pages.ToList();
            return true;
        }

    }
}
