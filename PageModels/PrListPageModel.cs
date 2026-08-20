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
    [QueryProperty(nameof(ActionParam), "action")]
    public partial class PrListPageModel : ObservableObject, IDisposable
    {
        private readonly IPurchaseRequisitionRepository _prRepo;
        private readonly ICustomColumnRepository _customColumnRepo;
        private readonly ICsvExportService _csvExportService;
        private readonly IPcrExportService _pcrExportService;
        private readonly ISettingsService _settingsService;
        private readonly IErrorHandler _errorHandler;

        private List<PurchaseRequisition> _allPrs = new();
        public IReadOnlyList<PurchaseRequisition> AllPrs => _allPrs;
        private CancellationTokenSource? _searchDebounce;

        [ObservableProperty]
        public partial ObservableCollection<PurchaseRequisition> FilteredPrs { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<CustomColumnDefinition> CustomColumnDefinitions { get; set; } = new();

        [ObservableProperty]
        public partial string SearchText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SelectedStatusFilter { get; set; } = "All";

        [ObservableProperty]
        public partial bool FilterOverdueOnly { get; set; }

        [ObservableProperty]
        public partial bool FilterPcrPendingOnly { get; set; }

        [ObservableProperty]
        public partial bool FilterUrgentOnly { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsStatusMessageVisible { get; set; }

        // Pagination
        [ObservableProperty]
        public partial int CurrentPage { get; set; } = 1;

        [ObservableProperty]
        public partial int PageSize { get; set; } = 10;

        [ObservableProperty]
        public partial int TotalPages { get; set; } = 1;

        [ObservableProperty]
        public partial int TotalFilteredCount { get; set; }

        [ObservableProperty]
        public partial string PaginationSummary { get; set; } = "Showing 0 requisitions";

        [ObservableProperty]
        public partial bool CanGoPrevious { get; set; }

        [ObservableProperty]
        public partial bool CanGoNext { get; set; }

        public List<int> PageSizeOptions { get; } = new() { 5, 10, 20, 50 };

        // Modal Form State (Add / Edit PR)
        [ObservableProperty]
        public partial bool IsEditModalVisible { get; set; }

        [ObservableProperty]
        public partial string EditModalTitle { get; set; } = "Edit Requisition";

        [ObservableProperty]
        public partial PurchaseRequisition? CurrentEditingPr { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<CustomFieldValue> EditingCustomValues { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<PrItem> EditingPrItems { get; set; } = new();

        // Bulk / Multi-PR Creation State
        [ObservableProperty]
        public partial bool IsBatchCreateModalVisible { get; set; }

        [ObservableProperty]
        public partial string BatchSharedRequestor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BatchSharedPlant { get; set; } = ProcurementPlant.RW01;

        [ObservableProperty]
        public partial string BatchSharedPrType { get; set; } = ProcurementPrType.StoresAndSpares;

        [ObservableProperty]
        public partial string BatchSharedPriority { get; set; } = ProcurementPriority.Normal;

        [ObservableProperty]
        public partial string BatchSharedStatus { get; set; } = ProcurementStatus.PrRaised;

        [ObservableProperty]
        public partial string BatchSharedNotes { get; set; } = string.Empty;

        [ObservableProperty]
        public partial ObservableCollection<CustomFieldValue> BatchSharedCustomValues { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<BatchPrEntry> BatchPrEntries { get; set; } = new();

        [ObservableProperty]
        public partial string BatchEntriesSummary { get; set; } = "3 requisitions ready";

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

        public List<string> StatusFilterOptions { get; } = new()
        {
            "All",
            ProcurementStatus.PrRaised,
            ProcurementStatus.RfqSent,
            ProcurementStatus.QuotesReceived,
            ProcurementStatus.PcrSubmitted,
            ProcurementStatus.PcrApproved,
            ProcurementStatus.PoRaised,
            ProcurementStatus.PartiallyDelivered,
            ProcurementStatus.Delivered,
            ProcurementStatus.Closed,
            ProcurementStatus.Merged,
            ProcurementStatus.OnHold,
            ProcurementStatus.Cancelled
        };

        public string[] AllStatuses => ProcurementStatus.AllStatuses;
        public string[] AllPriorities => ProcurementPriority.AllPriorities;
        public string[] AllPlants => ProcurementPlant.AllPlants;
        public string[] AllPrTypes => ProcurementPrType.AllPrTypes;

        public string? ActionParam
        {
            set
            {
                if (value == "new" || value == "bulk")
                {
                    _ = OpenBatchCreateModalAsync();
                }
            }
        }

        public PrListPageModel(
            IPurchaseRequisitionRepository prRepo,
            ICustomColumnRepository customColumnRepo,
            ICsvExportService csvExportService,
            IPcrExportService pcrExportService,
            ISettingsService settingsService,
            IErrorHandler errorHandler)
        {
            _prRepo = prRepo;
            _customColumnRepo = customColumnRepo;
            _csvExportService = csvExportService;
            _pcrExportService = pcrExportService;
            _settingsService = settingsService;
            _errorHandler = errorHandler;

            _settingsService.SettingsChanged += OnSettingsChanged;
            if (Application.Current != null)
            {
                Application.Current.RequestedThemeChanged += OnAppRequestedThemeChanged;
            }
        }

        // Registered as a DI singleton, so the container disposes it at shutdown — that is the
        // only point at which these two subscriptions may be released.
        public void Dispose()
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
            if (Application.Current != null)
            {
                Application.Current.RequestedThemeChanged -= OnAppRequestedThemeChanged;
            }
            _searchDebounce?.Cancel();
            _searchDebounce?.Dispose();
            _searchDebounce = null;
        }

        private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
        {
            switch (e.Key)
            {
                // Thresholds feed the overdue filter and the banner only — nothing on a card is bound to them.
                case nameof(ISettingsService.NormalOverdueDays):
                case nameof(ISettingsService.UrgentOverdueDays):
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        ApplyFilters();
                        UpdateStatusBanner();
                    });
                    break;

                // Theme repaints every converter-bound brush; currency reformats every money label.
                case nameof(ISettingsService.AppTheme):
                case nameof(ISettingsService.AccentTheme):
                case nameof(ISettingsService.DefaultCurrency):
                    RefreshCardVisuals();
                    break;
            }
        }

        private void OnAppRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => RefreshCardVisuals();

        private int _cardVisualsRefreshQueued;

        private void RefreshCardVisuals()
        {
            // Setting AppTheme raises BOTH SettingsChanged and Application.RequestedThemeChanged, so a
            // single theme click lands here twice. Coalesce — one queued pass repaints everything.
            if (Interlocked.CompareExchange(ref _cardVisualsRefreshQueued, 1, 0) != 0) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Interlocked.Exchange(ref _cardVisualsRefreshQueued, 0);

                OnPropertyChanged(nameof(FilterOverdueOnly));
                OnPropertyChanged(nameof(FilterPcrPendingOnly));
                OnPropertyChanged(nameof(FilterUrgentOnly));

                // Only the current page is bound; off-page PRs repaint when ApplyFilters brings them in.
                foreach (var pr in FilteredPrs)
                {
                    pr.NotifyHierarchyChanged();
                }
            });
        }

        [RelayCommand]
        public async Task LoadPrsAsync()
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;

                // Run the DB work on the thread pool to avoid blocking the UI
                var defs = await Task.Run(() => _customColumnRepo.GetAllDefinitionsAsync()).ConfigureAwait(true);
                CustomColumnDefinitions = new ObservableCollection<CustomColumnDefinition>(defs);

                // Load PRs on the thread pool. No NotifyHierarchyChanged pass here: nothing is bound
                // to these objects yet (FilteredPrs is populated below), so the ~861 PropertyChanged
                // events it raised had no subscribers. GetAllAsync already computed fulfilments.
                var loadedPrs = await Task.Run(() => _prRepo.GetAllAsync()).ConfigureAwait(true);

                // Back on UI thread: assign _allPrs and update bound collections
                _allPrs = loadedPrs;
                foreach (var pr in _allPrs)
                {
                    pr.PropertyChanged -= OnPrItemPropertyChanged;
                    pr.PropertyChanged += OnPrItemPropertyChanged;
                }
                ApplyFilters();
                UpdateStatusBanner();
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

        partial void OnSearchTextChanged(string value)
        {
            // Debounce search — cancel any pending filter and wait 300ms
            // to avoid re-filtering on every single keystroke
            _searchDebounce?.Cancel();
            _searchDebounce?.Dispose();
            _searchDebounce = new CancellationTokenSource();
            var token = _searchDebounce.Token;

            Task.Delay(300, token).ContinueWith(_ =>
            {
                if (!token.IsCancellationRequested)
                    MainThread.BeginInvokeOnMainThread(() => ApplyFilters(true));
            }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        partial void OnSelectedStatusFilterChanged(string value) => ApplyFilters(true);
        partial void OnFilterOverdueOnlyChanged(bool value) => ApplyFilters(true);
        partial void OnFilterPcrPendingOnlyChanged(bool value) => ApplyFilters(true);
        partial void OnFilterUrgentOnlyChanged(bool value) => ApplyFilters(true);

        partial void OnPageSizeChanged(int value)
        {
            CurrentPage = 1;
            ApplyFilters();
        }

        [RelayCommand]
        public void NextPage()
        {
            if (CurrentPage < TotalPages)
            {
                CurrentPage++;
                ApplyFilters();
            }
        }

        [RelayCommand]
        public void PreviousPage()
        {
            if (CurrentPage > 1)
            {
                CurrentPage--;
                ApplyFilters();
            }
        }

        [RelayCommand]
        public void FirstPage()
        {
            if (CurrentPage != 1)
            {
                CurrentPage = 1;
                ApplyFilters();
            }
        }

        [RelayCommand]
        public void LastPage()
        {
            if (CurrentPage != TotalPages)
            {
                CurrentPage = TotalPages;
                ApplyFilters();
            }
        }

        [RelayCommand]
        public void ToggleFilterOverdue() => FilterOverdueOnly = !FilterOverdueOnly;

        [RelayCommand]
        public void ToggleFilterPcrPending() => FilterPcrPendingOnly = !FilterPcrPendingOnly;

        [RelayCommand]
        public void ToggleFilterUrgent() => FilterUrgentOnly = !FilterUrgentOnly;

        [RelayCommand]
        public void DismissStatusMessage() => IsStatusMessageVisible = false;

        private void ApplyFilters(bool resetPage = false)
        {
            if (resetPage)
            {
                CurrentPage = 1;
            }

            var query = _allPrs.AsEnumerable();

            // Search text
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var term = SearchText.Trim().ToLowerInvariant();
                query = query.Where(p =>
                    p.PrNo.ToLowerInvariant().Contains(term) ||
                    (!string.IsNullOrEmpty(p.ConsolidatedFrom) && p.ConsolidatedFrom.ToLowerInvariant().Contains(term)) ||
                    p.Description.ToLowerInvariant().Contains(term) ||
                    p.Requestor.ToLowerInvariant().Contains(term) ||
                    p.Items.Any(i => i.ItemName.ToLowerInvariant().Contains(term) || i.Notes.ToLowerInvariant().Contains(term)) ||
                    p.Rfqs.Any(r => r.Vendor.ToLowerInvariant().Contains(term) || (!string.IsNullOrEmpty(r.RfqNo) && r.RfqNo.ToLowerInvariant().Contains(term))) ||
                    p.Pos.Any(po => po.Vendor.ToLowerInvariant().Contains(term) || po.PoNo.ToLowerInvariant().Contains(term)));
            }

            // Status filter
            if (!string.IsNullOrWhiteSpace(SelectedStatusFilter) && SelectedStatusFilter != "All")
            {
                query = query.Where(p => p.Status.Equals(SelectedStatusFilter, StringComparison.OrdinalIgnoreCase));
            }
            else if (string.IsNullOrWhiteSpace(SearchText))
            {
                // By default hide merged PRs unless explicitly searching or filtered
                query = query.Where(p => p.Status != ProcurementStatus.Merged);
            }

            // Overdue filter
            if (FilterOverdueOnly)
            {
                var normalDays = _settingsService.NormalOverdueDays;
                var urgentDays = _settingsService.UrgentOverdueDays;
                query = query.Where(p => p.IsOverdue(normalDays, urgentDays));
            }

            // PCR Pending filter
            if (FilterPcrPendingOnly)
            {
                query = query.Where(p => p.Pcr != null && !p.Pcr.IsFullyApproved);
            }

            // Urgent filter
            if (FilterUrgentOnly)
            {
                query = query.Where(p => p.IsUrgent);
            }

            var allFiltered = query.ToList();
            TotalFilteredCount = allFiltered.Count;
            TotalPages = Math.Max(1, (int)Math.Ceiling(TotalFilteredCount / (double)PageSize));

            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }
            if (CurrentPage < 1)
            {
                CurrentPage = 1;
            }

            CanGoPrevious = CurrentPage > 1;
            CanGoNext = CurrentPage < TotalPages;

            var startIndex = (CurrentPage - 1) * PageSize;
            var pagedList = allFiltered.Skip(startIndex).Take(PageSize).ToList();

            if (TotalFilteredCount == 0)
            {
                PaginationSummary = "No requisitions found";
            }
            else
            {
                var endIndex = Math.Min(startIndex + PageSize, TotalFilteredCount);
                PaginationSummary = $"Showing {startIndex + 1}–{endIndex} of {TotalFilteredCount} requisitions";
            }

            // Reconcile the existing collection instead of replacing the instance: replacing it makes the
            // bound BindableLayout tear down and rebuild every card, which also wipes each card's expanded
            // and selected state. Removals first, then insert/move into position.
            // ponytail: O(n^2) diff via IndexOf — fine while a page is PageSizeOptions-sized (max 50);
            // swap in an index map if paging ever goes into the hundreds.
            if (!FilteredPrs.SequenceEqual(pagedList))
            {
                for (var i = FilteredPrs.Count - 1; i >= 0; i--)
                {
                    if (!pagedList.Contains(FilteredPrs[i]))
                        FilteredPrs.RemoveAt(i);
                }

                // Each insert makes the bound layout build a whole card synchronously (~75ms on a
                // Debug build), so filling a full page in one pass freezes the UI for most of a
                // second. Place the first rows now and let the rest arrive over following ticks:
                // the board paints almost immediately and completes while the user is reading it.
                var generation = ++_fillGeneration;
                FillPage(pagedList, 0, generation);
            }

            // No UpdateSelectionState() here: filtering never touches pr.IsSelected, and the method
            // scans _allPrs rather than the page, so its result cannot change. The checkbox handler
            // and the batch commands call it directly when selection actually moves.
        }


        // Rows placed before yielding to the UI thread. Enough to fill the top of the viewport so
        // the board looks populated immediately; the rest stream in behind it.
        private const int FirstFillBatch = 3;
        private const int FillBatchSize = 2;

        // Bumped on every ApplyFilters so an in-flight fill from a superseded filter stops quietly.
        private int _fillGeneration;

        private void FillPage(List<PurchaseRequisition> pagedList, int start, int generation)
        {
            if (generation != _fillGeneration) return;

            var take = start == 0 ? FirstFillBatch : FillBatchSize;
            var end = Math.Min(start + take, pagedList.Count);

            for (var i = start; i < end; i++)
            {
                var existing = FilteredPrs.IndexOf(pagedList[i]);
                if (existing < 0)
                    FilteredPrs.Insert(i, pagedList[i]);
                else if (existing != i)
                    FilteredPrs.Move(existing, i);
            }

            if (end < pagedList.Count)
            {
                MainThread.BeginInvokeOnMainThread(() => FillPage(pagedList, end, generation));
            }
        }

        private void UpdateStatusBanner()
        {
            var normalDays = _settingsService.NormalOverdueDays;
            var urgentDays = _settingsService.UrgentOverdueDays;

            // One walk, two counters — this used to run Count() twice over the same list.
            var overdue = 0;
            var pcrPending = 0;
            foreach (var p in _allPrs)
            {
                if (p.IsOverdue(normalDays, urgentDays)) overdue++;
                if (p.Pcr != null && !p.Pcr.IsFullyApproved) pcrPending++;
            }

            if (overdue > 0 || pcrPending > 0)
            {
                StatusMessage = $"Attention: {overdue} PR(s) are overdue past SLA threshold and {pcrPending} PCR(s) are awaiting signature.";
                IsStatusMessageVisible = true;
            }
            else
            {
                IsStatusMessageVisible = false;
            }
        }

        [RelayCommand]
        public async Task ChangePrStatusAsync(PurchaseRequisition pr)
        {
            if (Shell.Current == null) return;

            var selected = await Shell.Current.DisplayActionSheetAsync(
                $"Update Status for {pr.PrNo}",
                "Cancel",
                null,
                ProcurementStatus.AllStatuses);

            if (selected != null && selected != "Cancel" && selected != pr.Status)
            {
                await UpdatePrStatusDirectAsync(pr, selected);
            }
        }

        public async Task UpdatePrStatusDirectAsync(PurchaseRequisition pr, string newStatus)
        {
            if (string.IsNullOrWhiteSpace(newStatus) || pr.Status == newStatus) return;
            try
            {
                pr.Status = newStatus;
                await _prRepo.SavePrFieldsAsync(pr);
                pr.NotifyStatusChanged();
                ApplyFilters();
                UpdateStatusBanner();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public async Task UpdatePoStatusDirectAsync(PurchaseOrder po, string newStatus)
        {
            if (string.IsNullOrWhiteSpace(newStatus) || po.Status == newStatus) return;
            try
            {
                po.Status = newStatus;
                await _prRepo.SavePoAsync(po);

                var parentPr = _allPrs.FirstOrDefault(p => p.Id == po.PrId);
                if (parentPr != null)
                {
                    if (parentPr.Pos.All(p => p.Status == PoStatus.Delivered))
                    {
                        parentPr.Status = ProcurementStatus.Delivered;
                        await _prRepo.SavePrFieldsAsync(parentPr);
                    }
                    else if (parentPr.Pos.Any(p => p.Status == PoStatus.Delivered))
                    {
                        parentPr.Status = ProcurementStatus.PartiallyDelivered;
                        await _prRepo.SavePrFieldsAsync(parentPr);
                    }

                    parentPr.NotifyHierarchyChanged();
                    UpdateStatusBanner();
                }

                ApplyFilters();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public async Task UpdateRfqStatusDirectAsync(RequestForQuotation rfq, string newStatus)
        {
            if (string.IsNullOrWhiteSpace(newStatus) || rfq.Status == newStatus) return;
            try
            {
                rfq.Status = newStatus;
                if (newStatus == RfqStatus.QuoteReceived && !rfq.QuoteReceivedDate.HasValue)
                {
                    rfq.QuoteReceivedDate = DateTime.Now;
                }

                await _prRepo.SaveRfqAsync(rfq);

                var parentPr = _allPrs.FirstOrDefault(p => p.Id == rfq.PrId);
                if (parentPr != null)
                {
                    if (parentPr.Rfqs.All(r => r.IsQuoteReceived) && parentPr.Status == ProcurementStatus.RfqSent)
                    {
                        parentPr.Status = ProcurementStatus.QuotesReceived;
                        await _prRepo.SavePrFieldsAsync(parentPr);
                    }

                    parentPr.NotifyHierarchyChanged();
                    UpdateStatusBanner();
                }

                ApplyFilters();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task TogglePriorityAsync(PurchaseRequisition pr)
        {
            try
            {
                pr.Priority = pr.Priority == ProcurementPriority.Urgent
                    ? ProcurementPriority.Normal
                    : ProcurementPriority.Urgent;

                await _prRepo.SavePrFieldsAsync(pr);
                pr.NotifyStatusChanged();
                ApplyFilters();
                UpdateStatusBanner();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void ToggleExpand(PurchaseRequisition pr)
        {
            pr.IsExpanded = !pr.IsExpanded;
        }

        // ================= PR CRUD =================

        [RelayCommand]
        public void AddEditingPrItem()
        {
            EditingPrItems.Add(new PrItem
            {
                Id = Guid.NewGuid(),
                PrId = CurrentEditingPr?.Id ?? Guid.NewGuid(),
                ItemName = string.Empty,
                Quantity = 1,
                Unit = "pcs",
                SortOrder = EditingPrItems.Count
            });
        }

        [RelayCommand]
        public void RemoveEditingPrItem(PrItem item)
        {
            if (EditingPrItems.Contains(item))
            {
                EditingPrItems.Remove(item);
            }
        }

        [RelayCommand]
        public async Task PasteEditingPrItemsFromClipboardAsync()
        {
            try
            {
                if (!Clipboard.Default.HasText)
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Clipboard Empty", "No text found on clipboard. Please copy lines from Excel first.", "OK");
                    return;
                }

                var text = await Clipboard.Default.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Clipboard Empty", "Clipboard text is empty.", "OK");
                    return;
                }

                var prId = CurrentEditingPr?.Id ?? Guid.NewGuid();
                var parsedItems = ClipboardItemParser.ParsePrItems(text, prId, EditingPrItems.Count);

                if (parsedItems.Count == 0)
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("No Items Detected", "Could not detect valid items from clipboard text.", "OK");
                    return;
                }

                // If only 1 placeholder blank row exists, replace it
                if (EditingPrItems.Count == 1 && string.IsNullOrWhiteSpace(EditingPrItems[0].ItemName))
                {
                    EditingPrItems.Clear();
                }

                foreach (var item in parsedItems)
                {
                    EditingPrItems.Add(item);
                }

                if (CurrentEditingPr != null && string.IsNullOrWhiteSpace(CurrentEditingPr.Description) && EditingPrItems.Count > 0)
                {
                    CurrentEditingPr.Description = EditingPrItems[0].ItemName;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task OpenEditPrModalAsync(PurchaseRequisition pr)
        {
            var defs = await _customColumnRepo.GetAllDefinitionsAsync();
            CustomColumnDefinitions = new ObservableCollection<CustomColumnDefinition>(defs);

            EditModalTitle = $"Edit Requisition {pr.PrNo}";
            CurrentEditingPr = pr;

            // Prepare custom fields with existing values
            var vals = new List<CustomFieldValue>();
            foreach (var col in CustomColumnDefinitions)
            {
                var existing = pr.CustomValues.FirstOrDefault(v => v.ColumnId == col.Id);
                vals.Add(new CustomFieldValue
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    PrId = pr.Id,
                    ColumnId = col.Id,
                    ColumnName = col.Name,
                    ColumnDataType = col.DataType,
                    SelectOptions = col.SelectOptions,
                    Value = existing?.Value ?? string.Empty
                });
            }
            EditingCustomValues = new ObservableCollection<CustomFieldValue>(vals);

            // Prepare line items
            if (pr.Items != null && pr.Items.Count > 0)
            {
                var copied = pr.Items.Select(i => new PrItem
                {
                    Id = i.Id,
                    PrId = i.PrId,
                    ItemName = i.ItemName,
                    Quantity = i.Quantity,
                    Unit = string.IsNullOrWhiteSpace(i.Unit) ? "pcs" : i.Unit,
                    EstimatedUnitPrice = i.EstimatedUnitPrice,
                    Notes = i.Notes,
                    SortOrder = i.SortOrder
                }).ToList();
                EditingPrItems = new ObservableCollection<PrItem>(copied);
            }
            else
            {
                EditingPrItems = new ObservableCollection<PrItem>
                {
                    new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = pr.Id,
                        ItemName = string.IsNullOrWhiteSpace(pr.Description) ? string.Empty : pr.Description,
                        Quantity = 1,
                        Unit = "pcs",
                        SortOrder = 0
                    }
                };
            }

            IsEditModalVisible = true;
        }

        [RelayCommand]
        public async Task SavePrModalAsync()
        {
            if (CurrentEditingPr == null) return;

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.PrNo))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "PR Number is required.", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.PrType))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "PR Type is required. Please select a PR Type.", "OK");
                return;
            }

            var validItems = EditingPrItems.Where(i => !string.IsNullOrWhiteSpace(i.ItemName)).ToList();
            CurrentEditingPr.Items = new ObservableCollection<PrItem>(validItems);

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.Description) && validItems.Count > 0)
            {
                CurrentEditingPr.Description = CurrentEditingPr.ItemsSummary;
            }

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.Description))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Description or at least one Line Item is required.", "OK");
                return;
            }

            try
            {
                CurrentEditingPr.CustomValues = new ObservableCollection<CustomFieldValue>(EditingCustomValues);
                await _prRepo.SaveAsync(CurrentEditingPr);
                CurrentEditingPr.NotifyHierarchyChanged();
                ApplyFilters();
                UpdateStatusBanner();

                IsEditModalVisible = false;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void CloseEditModal()
        {
            IsEditModalVisible = false;
        }

        [RelayCommand]
        public async Task DeletePrAsync(PurchaseRequisition pr)
        {
            if (Shell.Current == null) return;

            var confirm = await Shell.Current.DisplayAlertAsync(
                "Delete PR",
                $"Are you sure you want to delete {pr.PrNo} ({pr.Description}) and all associated RFQs, PCRs, and POs?",
                "Delete",
                "Cancel");

            if (!confirm) return;

            try
            {
                await _prRepo.DeleteAsync(pr.Id);
                pr.PropertyChanged -= OnPrItemPropertyChanged;
                _allPrs.Remove(pr);
                ApplyFilters();
                UpdateStatusBanner();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

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
            NewRfqNo = $"RFQ-{pr.PrNo.Replace("PR-", "")}-{(char)('A' + pr.Rfqs.Count)}";
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
            TargetPrForRfq = AllPrs.FirstOrDefault(p => p.Id == rfq.PrId);
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
                TargetPrForRfq = AllPrs.FirstOrDefault(p => p.Id == EditingRfq.PrId);
            }

            if (TargetPrForRfq == null) return;

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
                var parentPr = _allPrs.FirstOrDefault(p => p.Id == rfq.PrId);
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
                var parentPr = _allPrs.FirstOrDefault(p => p.Id == rfq.PrId);
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
                var parentPr = _allPrs.FirstOrDefault(p => p.Pcr != null && p.Pcr.Approvals.Any(a => a.Id == approval.Id))
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
                int poSequence = pr.Pos.Count + 101;

                for (int i = 0; i < pr.Rfqs.Count; i++)
                {
                    var rfq = pr.Rfqs[i];
                    var existingPo = pr.Pos?.FirstOrDefault(p => (p.LinkedRfqId.HasValue && p.LinkedRfqId.Value == rfq.Id) || (!string.IsNullOrWhiteSpace(p.Vendor) && !string.IsNullOrWhiteSpace(rfq.Vendor) && string.Equals(p.Vendor.Trim(), rfq.Vendor.Trim(), StringComparison.OrdinalIgnoreCase)));

                    PoRfqSelection selection;
                    if (existingPo != null)
                    {
                        selection = new PoRfqSelection(existingPo, rfq, pr)
                        {
                            OnTotalsRecalculated = RecalculatePoModalTotals
                        };
                    }
                    else
                    {
                        selection = new PoRfqSelection(rfq, pr, isSelected: true)
                        {
                            PoNo = $"PO-{DateTime.Now.Year}-{poSequence + i}",
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
            var pr = _allPrs.FirstOrDefault(p => p.Id == po.PrId) ?? FilteredPrs.FirstOrDefault(p => p.Id == po.PrId);
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
                OnPropertyChanged(nameof(SelectedPoRfqCount));
                OnPropertyChanged(nameof(SelectedPoRfqCountMessage));
                OnPropertyChanged(nameof(TotalPoAmountSummary));
                OnPropertyChanged(nameof(CanGoToPoStep2));
                OnPropertyChanged(nameof(HasPoQuantityValidationErrors));
                OnPropertyChanged(nameof(PoQuantityValidationErrorMessage));
                OnPropertyChanged(nameof(PoAllocationSummaryText));
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
                    .Select(g => $"{g.Key} {g.Sum(s => s.DisplayTotalAmount):N0}");
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

                foreach (var prItem in TargetPrForPo.Items)
                {
                    var otherPoOrdered = TargetPrForPo.Pos
                        .Where(p => !IsEditPoMode || (EditingPo != null && p.Id != EditingPo.Id))
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

            OnPropertyChanged(nameof(SelectedPoRfqCount));
            OnPropertyChanged(nameof(SelectedPoRfqCountMessage));
            OnPropertyChanged(nameof(TotalPoAmountSummary));
            OnPropertyChanged(nameof(CanGoToPoStep2));
            OnPropertyChanged(nameof(HasPoQuantityValidationErrors));
            OnPropertyChanged(nameof(PoQuantityValidationErrorMessage));
            OnPropertyChanged(nameof(PoAllocationSummaryText));
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
                if (IsEditPoMode && EditingPo != null)
                {
                    // EDIT EXISTING PO MODE
                    var rfqSel = selectedRfqs[0];
                    EditingPo.PoNo = rfqSel.PoNo.Trim();
                    EditingPo.Vendor = rfqSel.VendorName.Trim();
                    EditingPo.Value = rfqSel.DisplayTotalAmount;
                    EditingPo.Currency = string.IsNullOrWhiteSpace(rfqSel.Currency) ? "AED" : rfqSel.Currency;
                    EditingPo.BaseAmount = rfqSel.BaseAmount;
                    EditingPo.Freight = rfqSel.Freight;
                    EditingPo.OtherCharges = rfqSel.OtherCharges;
                    EditingPo.Discount = rfqSel.OverallDiscount;
                    EditingPo.VatType = rfqSel.VatType;

                    // Update PO items
                    EditingPo.Items.Clear();
                    if (rfqSel.HasItems)
                    {
                        foreach (var itemSel in rfqSel.Items.Where(i => i.IsSelected))
                        {
                            EditingPo.Items.Add(new PurchaseOrderItem
                            {
                                Id = itemSel.Id,
                                PoId = EditingPo.Id,
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

                    await _prRepo.SavePoAsync(EditingPo);

                    // If linked RFQ exists and user modified items, persist to RFQ
                    if (rfqSel.Rfq != null && rfqSel.HasItems)
                    {
                        foreach (var itemSel in rfqSel.Items)
                        {
                            var matchingRfqItem = rfqSel.Rfq.Items?.FirstOrDefault(i => (itemSel.RfqItemId.HasValue && i.Id == itemSel.RfqItemId.Value) || string.Equals(i.ItemName, itemSel.ItemName, StringComparison.OrdinalIgnoreCase));
                            if (matchingRfqItem != null)
                            {
                                matchingRfqItem.Quantity = itemSel.Quantity;
                                matchingRfqItem.QuotedUnitPrice = itemSel.QuotedUnitPrice;
                                matchingRfqItem.Discount = itemSel.Discount;
                                matchingRfqItem.IsQuoted = itemSel.IsSelected;
                            }
                            else if (itemSel.IsSelected && itemSel.QuotedUnitPrice.HasValue && itemSel.QuotedUnitPrice.Value > 0)
                            {
                                var newRfqItem = new RfqItem
                                {
                                    Id = itemSel.RfqItemId ?? Guid.NewGuid(),
                                    RfqId = rfqSel.Rfq.Id,
                                    PrItemId = itemSel.PrItemId,
                                    ItemName = itemSel.ItemName,
                                    Quantity = itemSel.Quantity,
                                    Unit = itemSel.Unit,
                                    IsQuoted = true,
                                    QuotedUnitPrice = itemSel.QuotedUnitPrice,
                                    Discount = itemSel.Discount
                                };
                                rfqSel.Rfq.Items?.Add(newRfqItem);
                                itemSel.RfqItemId = newRfqItem.Id;
                            }
                        }
                        rfqSel.Rfq.QuoteAmount = rfqSel.BaseAmount;
                        if (rfqSel.Freight.HasValue) rfqSel.Rfq.Freight = rfqSel.Freight;
                        if (rfqSel.OtherCharges.HasValue) rfqSel.Rfq.OtherCharges = rfqSel.OtherCharges;
                        if (rfqSel.OverallDiscount.HasValue) rfqSel.Rfq.Discount = rfqSel.OverallDiscount;
                        if (!string.IsNullOrWhiteSpace(rfqSel.VatType)) rfqSel.Rfq.VatType = rfqSel.VatType;

                        rfqSel.Rfq.NotifyCalculationsChanged();
                        await _prRepo.SaveRfqAsync(rfqSel.Rfq);
                    }
                }
                else
                {
                    // CREATE NEW PO(S) MODE
                    foreach (var rfqSel in selectedRfqs)
                    {
                        var po = new PurchaseOrder
                        {
                            Id = Guid.NewGuid(),
                            PrId = TargetPrForPo.Id,
                            PoNo = rfqSel.PoNo.Trim(),
                            Vendor = rfqSel.VendorName.Trim(),
                            LinkedRfqId = rfqSel.Rfq?.Id,
                            Value = rfqSel.DisplayTotalAmount,
                            Currency = string.IsNullOrWhiteSpace(rfqSel.Currency) ? "AED" : rfqSel.Currency,
                            Status = PoStatus.Raised,
                            Date = DateTime.Today,
                            BaseAmount = rfqSel.BaseAmount,
                            Freight = rfqSel.Freight,
                            OtherCharges = rfqSel.OtherCharges,
                            Discount = rfqSel.OverallDiscount,
                            VatType = rfqSel.VatType
                        };

                        if (rfqSel.HasItems)
                        {
                            foreach (var itemSel in rfqSel.Items.Where(i => i.IsSelected))
                            {
                                po.Items.Add(new PurchaseOrderItem
                                {
                                    Id = Guid.NewGuid(),
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

                        await _prRepo.SavePoAsync(po);
                        TargetPrForPo.Pos.Add(po);

                        // If user updated item quantities or unit prices, persist them to RFQ items
                        if (rfqSel.HasItems && rfqSel.Rfq != null)
                        {
                            foreach (var itemSel in rfqSel.Items)
                            {
                                var matchingRfqItem = rfqSel.Rfq.Items?.FirstOrDefault(i => (itemSel.RfqItemId.HasValue && i.Id == itemSel.RfqItemId.Value) || string.Equals(i.ItemName, itemSel.ItemName, StringComparison.OrdinalIgnoreCase));
                                if (matchingRfqItem != null)
                                {
                                    matchingRfqItem.Quantity = itemSel.Quantity;
                                    matchingRfqItem.QuotedUnitPrice = itemSel.QuotedUnitPrice;
                                    matchingRfqItem.Discount = itemSel.Discount;
                                    matchingRfqItem.IsQuoted = itemSel.IsSelected;
                                }
                                else if (itemSel.IsSelected && itemSel.QuotedUnitPrice.HasValue && itemSel.QuotedUnitPrice.Value > 0)
                                {
                                    var newRfqItem = new RfqItem
                                    {
                                        Id = itemSel.RfqItemId ?? Guid.NewGuid(),
                                        RfqId = rfqSel.Rfq.Id,
                                        PrItemId = itemSel.PrItemId,
                                        ItemName = itemSel.ItemName,
                                        Quantity = itemSel.Quantity,
                                        Unit = itemSel.Unit,
                                        IsQuoted = true,
                                        QuotedUnitPrice = itemSel.QuotedUnitPrice,
                                        Discount = itemSel.Discount
                                    };
                                    rfqSel.Rfq.Items?.Add(newRfqItem);
                                    itemSel.RfqItemId = newRfqItem.Id;
                                }
                            }
                            rfqSel.Rfq.QuoteAmount = rfqSel.BaseAmount;
                            if (rfqSel.Freight.HasValue) rfqSel.Rfq.Freight = rfqSel.Freight;
                            if (rfqSel.OtherCharges.HasValue) rfqSel.Rfq.OtherCharges = rfqSel.OtherCharges;
                            if (rfqSel.OverallDiscount.HasValue) rfqSel.Rfq.Discount = rfqSel.OverallDiscount;
                            if (!string.IsNullOrWhiteSpace(rfqSel.VatType)) rfqSel.Rfq.VatType = rfqSel.VatType;

                            rfqSel.Rfq.NotifyCalculationsChanged();
                            await _prRepo.SaveRfqAsync(rfqSel.Rfq);
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

                var parentPr = _allPrs.FirstOrDefault(p => p.Id == po.PrId);
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
                var parentPr = _allPrs.FirstOrDefault(p => p.Id == po.PrId);
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

        // ================= CSV EXPORT =================

        [RelayCommand]
        public async Task ExportCsvAsync()
        {
            try
            {
                var csv = await _csvExportService.ExportPrsToCsvAsync(_allPrs, CustomColumnDefinitions);
                var filePath = await _csvExportService.SaveExportToFileAsync(csv);

                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Export Successful",
                        $"Procurement report successfully exported to:\n{filePath}",
                        "OK");
                }
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

                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Export Successful",
                        $"Price comparison spreadsheet exported to:\n{filePath}",
                        "OK");
                }
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

                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Export Successful",
                        $"Price comparison PDF document exported to:\n{filePath}",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ================= BATCH SELECTION & ACTIONS =================

        [RelayCommand]
        public void ClearSelection()
        {
            foreach (var pr in _allPrs)
            {
                pr.IsSelected = false;
            }
            UpdateSelectionState();
        }

        public void UpdateSelectionState()
        {
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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

            MergeMasterPrNo = cleanNumbers.Count > 0 ? string.Join("/", cleanNumbers) : $"PR-{DateTime.Now.Year}-{_allPrs.Count + 1:D3}";
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

            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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
                    Priority = MergePriority,
                    Status = ProcurementStatus.PrRaised,
                    Notes = MergeNotes.Trim(),
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    ConsolidatedFrom = string.Join(", ", selected.Select(p => p.PrNo))
                };

                await _prRepo.MergePrsAsync(selected, masterPr, MergeCopyChildRfqs);

                _allPrs.Insert(0, masterPr);
                masterPr.NotifyHierarchyChanged();
                foreach (var src in selected)
                {
                    src.IsSelected = false;
                    src.NotifyHierarchyChanged();
                }

                IsMergePrModalVisible = false;
                SelectedPrsForMerge.Clear();
                UpdateSelectionState();
                ApplyFilters();
                UpdateStatusBanner();

                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Consolidation Complete",
                        $"Successfully combined {selected.Count} requisitions into Master Requisition {masterPr.PrNo}.",
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

        // ================= DYNAMIC TOOLBAR ACTION ROUTING =================

        [RelayCommand]
        public async Task MergeOrSplitPrAsync()
        {
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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

            // Find all child PRs linked to this master PR
            var childPrs = _allPrs.Where(p => p.ParentPrId == masterPr.Id).ToList();

            // Fallback matching by ConsolidatedFrom PR numbers if needed
            if (childPrs.Count == 0 && !string.IsNullOrWhiteSpace(masterPr.ConsolidatedFrom))
            {
                var prNos = masterPr.ConsolidatedFrom.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                                     .Select(s => s.Trim().ToLowerInvariant())
                                                     .Where(s => !string.IsNullOrWhiteSpace(s))
                                                     .ToList();
                childPrs = _allPrs.Where(p => prNos.Contains(p.PrNo.ToLowerInvariant()) || prNos.Contains(p.PrNo.Replace("PR-", "").ToLowerInvariant())).ToList();
            }

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
                SplitActionSummary = $"All {totalCount} requisitions will be separated back to standalone active PRs. Master PR {SplitTargetMasterPr?.PrNo} will be removed.";
                CanConfirmSplit = true;
            }
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

                // Reload fresh PRs
                var freshPrs = await _prRepo.GetAllAsync();
                foreach (var p in _allPrs)
                    p.PropertyChanged -= OnPrItemPropertyChanged;

                _allPrs.Clear();
                foreach (var pr in freshPrs)
                {
                    pr.PropertyChanged += OnPrItemPropertyChanged;
                    _allPrs.Add(pr);
                }

                CloseSplitPrModal();
                UpdateSelectionState();
                ApplyFilters();
                UpdateStatusBanner();

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
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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
                foreach (var pr in _allPrs)
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
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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
                foreach (var pr in _allPrs)
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
                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Copied to Clipboard",
                        $"Successfully copied {selectedItems.Count} item(s) across selected PRs formatted for your email.\n\nYou can now paste directly into Outlook, Gmail, or Word.",
                        "OK");
                }
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
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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
            if (string.IsNullOrWhiteSpace(BatchRfqVendor))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Vendor name is required.", "OK");
                return;
            }

            var selected = _allPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;

            try
            {
                IsBusy = true;
                var rfqTemplate = new RequestForQuotation
                {
                    RfqNo = string.IsNullOrWhiteSpace(BatchRfqNo) ? $"RFQ-{DateTime.Now:yyyyMMdd}" : BatchRfqNo.Trim(),
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
            var selected = _allPrs.Where(p => p.IsSelected).ToList();
            if (selected.Count < 1)
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Combined PO", "Please select at least 1 requisition.", "OK");
                return;
            }

            var totalPos = _allPrs.SelectMany(p => p.Pos).Count();
            BatchPoNo = $"PO-{DateTime.Now.Year}-{totalPos + 101}";

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

            var selected = _allPrs.Where(p => p.IsSelected).ToList();
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

        // ================= BULK / MULTI-PR CREATION OPERATIONS =================

        [RelayCommand]
        public async Task OpenBatchCreateModalAsync()
        {
            var defs = await _customColumnRepo.GetAllDefinitionsAsync();
            CustomColumnDefinitions = new ObservableCollection<CustomColumnDefinition>(defs);

            var mostRecentRequestor = _allPrs.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Requestor))?.Requestor ?? string.Empty;
            BatchSharedRequestor = mostRecentRequestor;
            BatchSharedPlant = ProcurementPlant.RW01;
            BatchSharedPrType = ProcurementPrType.StoresAndSpares;
            BatchSharedPriority = ProcurementPriority.Normal;
            BatchSharedStatus = ProcurementStatus.PrRaised;
            BatchSharedNotes = string.Empty;

            // Prepare shared custom fields
            var sharedVals = new List<CustomFieldValue>();
            foreach (var col in CustomColumnDefinitions)
            {
                sharedVals.Add(new CustomFieldValue
                {
                    Id = Guid.NewGuid(),
                    ColumnId = col.Id,
                    ColumnName = col.Name,
                    ColumnDataType = col.DataType,
                    SelectOptions = col.SelectOptions,
                    Value = string.Empty
                });
            }
            BatchSharedCustomValues = new ObservableCollection<CustomFieldValue>(sharedVals);

            // Populate 3 initial PR rows
            var entries = new ObservableCollection<BatchPrEntry>();
            var startNum = _allPrs.Count + 1;
            for (int i = 0; i < 3; i++)
            {
                var entry = CreateNewBatchPrEntry(startNum + i, BatchSharedRequestor, BatchSharedPriority, BatchSharedPlant, BatchSharedPrType);
                entries.Add(entry);
            }

            BatchPrEntries = entries;
            UpdateBatchEntriesSummary();
            IsBatchCreateModalVisible = true;
        }

        private BatchPrEntry CreateNewBatchPrEntry(int sequenceNumber, string requestor, string priority, string plant = ProcurementPlant.RW01, string prType = ProcurementPrType.StoresAndSpares)
        {
            var entryId = Guid.NewGuid();
            var rowVals = new ObservableCollection<CustomFieldValue>();
            foreach (var sv in BatchSharedCustomValues)
            {
                rowVals.Add(new CustomFieldValue
                {
                    Id = Guid.NewGuid(),
                    PrId = entryId,
                    ColumnId = sv.ColumnId,
                    ColumnName = sv.ColumnName,
                    ColumnDataType = sv.ColumnDataType,
                    SelectOptions = sv.SelectOptions,
                    Value = sv.Value ?? string.Empty
                });
            }

            var entry = new BatchPrEntry
            {
                Id = entryId,
                PrNo = $"PR-{DateTime.Now.Year}-{sequenceNumber:D3}",
                Requestor = requestor,
                Plant = plant,
                PrType = prType,
                Priority = priority,
                Status = ProcurementStatus.PrRaised,
                Notes = BatchSharedNotes,
                CustomValues = rowVals,
                Items = new ObservableCollection<PrItem>
                {
                    new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = entryId,
                        ItemName = string.Empty,
                        Quantity = 1,
                        Unit = "pcs",
                        SortOrder = 0
                    }
                }
            };
            return entry;
        }

        [RelayCommand]
        public void AddBatchPrRow()
        {
            var nextNum = _allPrs.Count + BatchPrEntries.Count + 1;
            var entry = CreateNewBatchPrEntry(nextNum, BatchSharedRequestor, BatchSharedPriority, BatchSharedPlant, BatchSharedPrType);
            BatchPrEntries.Add(entry);
            UpdateBatchEntriesSummary();
        }

        [RelayCommand]
        public void RemoveBatchPrRow(BatchPrEntry row)
        {
            if (BatchPrEntries.Count <= 1) return;
            if (BatchPrEntries.Contains(row))
            {
                BatchPrEntries.Remove(row);
                UpdateBatchEntriesSummary();
            }
        }

        [RelayCommand]
        public void DuplicateBatchPrRow(BatchPrEntry source)
        {
            var nextNum = _allPrs.Count + BatchPrEntries.Count + 1;
            var newEntryId = Guid.NewGuid();

            var dupCustomVals = new ObservableCollection<CustomFieldValue>();
            foreach (var cv in source.CustomValues)
            {
                dupCustomVals.Add(new CustomFieldValue
                {
                    Id = Guid.NewGuid(),
                    PrId = newEntryId,
                    ColumnId = cv.ColumnId,
                    ColumnName = cv.ColumnName,
                    ColumnDataType = cv.ColumnDataType,
                    SelectOptions = cv.SelectOptions,
                    Value = cv.Value
                });
            }

            var dupItems = new ObservableCollection<PrItem>();
            int s = 0;
            foreach (var item in source.Items)
            {
                dupItems.Add(new PrItem
                {
                    Id = Guid.NewGuid(),
                    PrId = newEntryId,
                    ItemName = item.ItemName,
                    Quantity = item.Quantity,
                    Unit = item.Unit,
                    EstimatedUnitPrice = item.EstimatedUnitPrice,
                    Notes = item.Notes,
                    SortOrder = s++
                });
            }

            var duplicate = new BatchPrEntry
            {
                Id = newEntryId,
                PrNo = $"PR-{DateTime.Now.Year}-{nextNum:D3}",
                Description = source.Description,
                Requestor = source.Requestor,
                Plant = source.Plant,
                PrType = source.PrType,
                Priority = source.Priority,
                Status = source.Status,
                Notes = source.Notes,
                CustomValues = dupCustomVals,
                Items = dupItems
            };

            BatchPrEntries.Add(duplicate);
            UpdateBatchEntriesSummary();
        }

        [RelayCommand]
        public void ApplySharedFieldsToAll()
        {
            foreach (var entry in BatchPrEntries)
            {
                if (!string.IsNullOrWhiteSpace(BatchSharedRequestor))
                    entry.Requestor = BatchSharedRequestor.Trim();

                if (!string.IsNullOrWhiteSpace(BatchSharedPlant))
                    entry.Plant = BatchSharedPlant;

                if (!string.IsNullOrWhiteSpace(BatchSharedPrType))
                    entry.PrType = BatchSharedPrType;

                entry.Priority = BatchSharedPriority;

                if (!string.IsNullOrWhiteSpace(BatchSharedNotes))
                    entry.Notes = BatchSharedNotes.Trim();

                foreach (var sharedVal in BatchSharedCustomValues)
                {
                    if (!string.IsNullOrWhiteSpace(sharedVal.Value))
                    {
                        var target = entry.CustomValues.FirstOrDefault(v => v.ColumnId == sharedVal.ColumnId);
                        if (target != null)
                        {
                            target.Value = sharedVal.Value;
                        }
                    }
                }
            }
        }

        [RelayCommand]
        public void AddBatchItemToPr(BatchPrEntry entry)
        {
            entry.Items.Add(new PrItem
            {
                Id = Guid.NewGuid(),
                PrId = entry.Id,
                ItemName = string.Empty,
                Quantity = 1,
                Unit = "pcs",
                SortOrder = entry.Items.Count
            });
            entry.NotifyItemsChanged();
        }

        [RelayCommand]
        public void RemoveBatchItemFromPr(PrItem item)
        {
            foreach (var entry in BatchPrEntries)
            {
                if (entry.Items.Contains(item))
                {
                    if (entry.Items.Count > 1)
                    {
                        entry.Items.Remove(item);
                        entry.NotifyItemsChanged();
                    }
                    break;
                }
            }
        }

        private void UpdateBatchEntriesSummary()
        {
            var count = BatchPrEntries.Count;
            BatchEntriesSummary = count == 1 ? "1 requisition ready to create" : $"{count} requisitions ready to create";
        }

        [RelayCommand]
        public async Task SaveBatchPrsModalAsync()
        {
            if (BatchPrEntries.Count == 0) return;

            // Validate PR entries
            var validEntries = new List<PurchaseRequisition>();
            int index = 1;

            foreach (var entry in BatchPrEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.PrNo))
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Validation", $"Row {index}: PR Number is required.", "OK");
                    return;
                }

                // Filter valid line items
                var validItems = entry.Items.Where(i => !string.IsNullOrWhiteSpace(i.ItemName)).ToList();

                var desc = entry.Description?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(desc))
                {
                    if (validItems.Count > 0)
                    {
                        desc = string.Join(", ", validItems.Select(i => $"{i.ItemName} ({i.FormattedQuantity})"));
                    }
                    else
                    {
                        if (Shell.Current != null)
                            await Shell.Current.DisplayAlertAsync("Validation", $"Row {index} ({entry.PrNo}): Description or at least one Line Item is required.", "OK");
                        return;
                    }
                }

                var prType = string.IsNullOrWhiteSpace(entry.PrType) ? BatchSharedPrType : entry.PrType;
                if (string.IsNullOrWhiteSpace(prType))
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Validation", $"Row {index} ({entry.PrNo}): PR Type is required. Please select a PR Type.", "OK");
                    return;
                }

                var pr = new PurchaseRequisition
                {
                    Id = entry.Id,
                    PrNo = entry.PrNo.Trim(),
                    Description = desc,
                    Requestor = string.IsNullOrWhiteSpace(entry.Requestor) ? (string.IsNullOrWhiteSpace(BatchSharedRequestor) ? "Unassigned" : BatchSharedRequestor.Trim()) : entry.Requestor.Trim(),
                    Plant = string.IsNullOrWhiteSpace(entry.Plant) ? (string.IsNullOrWhiteSpace(BatchSharedPlant) ? ProcurementPlant.RW01 : BatchSharedPlant) : entry.Plant,
                    PrType = prType,
                    Priority = string.IsNullOrWhiteSpace(entry.Priority) ? BatchSharedPriority : entry.Priority,
                    Status = string.IsNullOrWhiteSpace(entry.Status) ? ProcurementStatus.PrRaised : entry.Status,
                    Notes = entry.Notes?.Trim() ?? string.Empty,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    Items = new ObservableCollection<PrItem>(validItems),
                    CustomValues = new ObservableCollection<CustomFieldValue>(entry.CustomValues)
                };

                validEntries.Add(pr);
                index++;
            }

            try
            {
                IsBusy = true;
                await _prRepo.SaveBatchPrsAsync(validEntries);

                // Insert into in-memory list
                for (int i = validEntries.Count - 1; i >= 0; i--)
                {
                    var pr = validEntries[i];
                    pr.NotifyHierarchyChanged();
                    _allPrs.Insert(0, pr);
                }

                IsBatchCreateModalVisible = false;
                BatchPrEntries.Clear();
                ApplyFilters();
                UpdateStatusBanner();

                if (Shell.Current != null)
                {
                    await Shell.Current.DisplayAlertAsync(
                        "Batch Creation Complete",
                        $"Successfully created {validEntries.Count} purchase requisitions.",
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

        [RelayCommand]
        public void CloseBatchCreateModal()
        {
            IsBatchCreateModalVisible = false;
            BatchPrEntries.Clear();
        }

        [RelayCommand]
        public async Task PasteBatchPrItemsFromClipboardAsync(BatchPrEntry? entry)
        {
            if (entry == null) return;
            try
            {
                if (!Clipboard.Default.HasText)
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Clipboard Empty", "No text found on clipboard. Please copy items from Excel first.", "OK");
                    return;
                }

                var text = await Clipboard.Default.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;

                var parsedItems = ClipboardItemParser.ParsePrItems(text, entry.Id, entry.Items.Count);
                if (parsedItems.Count == 0) return;

                // If only 1 placeholder blank row exists, replace it
                if (entry.Items.Count == 1 && string.IsNullOrWhiteSpace(entry.Items[0].ItemName))
                {
                    entry.Items.Clear();
                }

                foreach (var item in parsedItems)
                {
                    entry.Items.Add(item);
                }

                entry.NotifyItemsChanged();

                if (string.IsNullOrWhiteSpace(entry.Description) && entry.Items.Count > 0)
                {
                    entry.Description = entry.Items[0].ItemName;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task PasteBatchPrRowsFromClipboardAsync()
        {
            try
            {
                if (!Clipboard.Default.HasText)
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Clipboard Empty", "No text found on clipboard. Please copy lines from Excel first.", "OK");
                    return;
                }

                var text = await Clipboard.Default.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;

                var startSeq = _allPrs.Count + BatchPrEntries.Count + 1;
                var parsedEntries = ClipboardItemParser.ParseBatchPrEntries(
                    text,
                    startSeq,
                    BatchSharedRequestor,
                    BatchSharedPriority,
                    BatchSharedNotes,
                    BatchSharedCustomValues);

                if (parsedEntries.Count == 0)
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("No Requisitions Detected", "Could not detect valid requisitions from clipboard text.", "OK");
                    return;
                }

                // If current entries is only the initial blank rows with empty descriptions, clear them
                if (BatchPrEntries.Count <= 3 && BatchPrEntries.All(e => string.IsNullOrWhiteSpace(e.Description) && (e.Items.Count == 0 || string.IsNullOrWhiteSpace(e.Items[0].ItemName))))
                {
                    BatchPrEntries.Clear();
                }

                foreach (var parsedEntry in parsedEntries)
                {
                    // Skip if a row with this PR number is already in BatchPrEntries
                    if (!string.IsNullOrWhiteSpace(parsedEntry.PrNo) &&
                        BatchPrEntries.Any(e => string.Equals(e.PrNo?.Trim(), parsedEntry.PrNo.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    BatchPrEntries.Add(parsedEntry);
                }

                UpdateBatchEntriesSummary();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public void HandleInlineItemPaste(PrItem currentItem, string rawPastedText, ObservableCollection<PrItem> targetCollection)
        {
            if (string.IsNullOrWhiteSpace(rawPastedText))
                return;

            var parsed = ClipboardItemParser.ParsePrItems(rawPastedText, currentItem.PrId, currentItem.SortOrder);
            if (parsed.Count == 0) return;

            // Set current item to first parsed item's attributes
            var first = parsed[0];
            currentItem.ItemName = first.ItemName;
            currentItem.Quantity = first.Quantity;
            currentItem.Unit = first.Unit;
            currentItem.EstimatedUnitPrice = first.EstimatedUnitPrice;
            currentItem.Notes = first.Notes;

            // Insert subsequent parsed items right after current item
            int currentIndex = targetCollection.IndexOf(currentItem);
            if (currentIndex < 0) currentIndex = 0;

            for (int i = 1; i < parsed.Count; i++)
            {
                int targetIndex = currentIndex + i;
                if (targetIndex < targetCollection.Count && string.IsNullOrWhiteSpace(targetCollection[targetIndex].ItemName))
                {
                    targetCollection[targetIndex].ItemName = parsed[i].ItemName;
                    targetCollection[targetIndex].Quantity = parsed[i].Quantity;
                    targetCollection[targetIndex].Unit = parsed[i].Unit;
                    targetCollection[targetIndex].EstimatedUnitPrice = parsed[i].EstimatedUnitPrice;
                    targetCollection[targetIndex].Notes = parsed[i].Notes;
                }
                else if (targetIndex <= targetCollection.Count)
                {
                    targetCollection.Insert(targetIndex, parsed[i]);
                }
                else
                {
                    targetCollection.Add(parsed[i]);
                }
            }

            if (CurrentEditingPr != null && string.IsNullOrWhiteSpace(CurrentEditingPr.Description) && targetCollection.Count > 0)
            {
                CurrentEditingPr.Description = targetCollection[0].ItemName;
            }
        }

        public void HandleInlineQuantityPaste(PrItem currentItem, string rawPastedText, ObservableCollection<PrItem> targetCollection)
        {
            if (string.IsNullOrWhiteSpace(rawPastedText))
                return;

            var parsed = ClipboardItemParser.ParseQuantities(rawPastedText);
            if (parsed.Count == 0) return;

            // Set current item's quantity
            var first = parsed[0];
            currentItem.Quantity = first.Quantity;
            if (!string.IsNullOrWhiteSpace(first.Unit))
            {
                currentItem.Unit = first.Unit;
            }

            int currentIndex = targetCollection.IndexOf(currentItem);
            if (currentIndex < 0) currentIndex = 0;

            for (int i = 1; i < parsed.Count; i++)
            {
                int targetIndex = currentIndex + i;
                if (targetIndex < targetCollection.Count)
                {
                    targetCollection[targetIndex].Quantity = parsed[i].Quantity;
                    if (!string.IsNullOrWhiteSpace(parsed[i].Unit))
                    {
                        targetCollection[targetIndex].Unit = parsed[i].Unit!;
                    }
                }
                else
                {
                    targetCollection.Add(new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = currentItem.PrId,
                        ItemName = string.Empty,
                        Quantity = parsed[i].Quantity,
                        Unit = !string.IsNullOrWhiteSpace(parsed[i].Unit) ? parsed[i].Unit! : "pcs",
                        SortOrder = targetIndex
                    });
                }
            }
        }

        public void HandleInlineUnitPaste(PrItem currentItem, string rawPastedText, ObservableCollection<PrItem> targetCollection)
        {
            if (string.IsNullOrWhiteSpace(rawPastedText))
                return;

            var parsed = ClipboardItemParser.ParseUnits(rawPastedText);
            if (parsed.Count == 0) return;

            // Set current item's unit
            currentItem.Unit = parsed[0];

            int currentIndex = targetCollection.IndexOf(currentItem);
            if (currentIndex < 0) currentIndex = 0;

            for (int i = 1; i < parsed.Count; i++)
            {
                int targetIndex = currentIndex + i;
                if (targetIndex < targetCollection.Count)
                {
                    targetCollection[targetIndex].Unit = parsed[i];
                }
                else
                {
                    targetCollection.Add(new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = currentItem.PrId,
                        ItemName = string.Empty,
                        Quantity = 1,
                        Unit = parsed[i],
                        SortOrder = targetIndex
                    });
                }
            }
        }

        public void HandleBatchPrRowPaste(BatchPrEntry currentEntry, string rawPastedText, bool isPrNoColumn)
        {
            if (string.IsNullOrWhiteSpace(rawPastedText))
                return;

            var startSeq = _allPrs.Count + BatchPrEntries.Count + 1;
            var parsed = ClipboardItemParser.ParseBatchPrEntries(
                rawPastedText,
                startSeq,
                BatchSharedRequestor,
                BatchSharedPriority,
                BatchSharedNotes,
                BatchSharedCustomValues,
                isPrNoFirst: isPrNoColumn);

            if (parsed.Count == 0) return;

            // Set current entry attributes to first parsed row
            var first = parsed[0];
            if (isPrNoColumn)
            {
                currentEntry.PrNo = first.PrNo;
                if (!string.IsNullOrWhiteSpace(first.Description))
                    currentEntry.Description = first.Description;
            }
            else
            {
                currentEntry.Description = first.Description;
                if (first.PrNo != null && (first.PrNo.StartsWith("PR-", StringComparison.OrdinalIgnoreCase) || first.PrNo.StartsWith("PR#", StringComparison.OrdinalIgnoreCase)))
                    currentEntry.PrNo = first.PrNo;
            }

            if (!string.IsNullOrWhiteSpace(first.Requestor))
                currentEntry.Requestor = first.Requestor;
            if (!string.IsNullOrWhiteSpace(first.Priority))
                currentEntry.Priority = first.Priority;
            if (!string.IsNullOrWhiteSpace(first.Notes))
                currentEntry.Notes = first.Notes;

            // If current entry items was empty or blank and first has items with names, copy items
            if (first.Items.Count > 0 && !string.IsNullOrWhiteSpace(first.Items[0].ItemName))
            {
                if (currentEntry.Items.Count <= 1 && (currentEntry.Items.Count == 0 || string.IsNullOrWhiteSpace(currentEntry.Items[0].ItemName)))
                {
                    currentEntry.Items.Clear();
                    foreach (var itm in first.Items)
                    {
                        itm.PrId = currentEntry.Id;
                        currentEntry.Items.Add(itm);
                    }
                    currentEntry.NotifyItemsChanged();
                }
            }

            // Insert subsequent parsed PR rows directly after currentEntry, skipping duplicates
            int currentIndex = BatchPrEntries.IndexOf(currentEntry);
            if (currentIndex < 0) currentIndex = 0;
            int insertOffset = 1;

            for (int i = 1; i < parsed.Count; i++)
            {
                var candidate = parsed[i];

                // Skip if another row in BatchPrEntries already has this PR number
                if (!string.IsNullOrWhiteSpace(candidate.PrNo) &&
                    BatchPrEntries.Any(e => e != currentEntry && string.Equals(e.PrNo?.Trim(), candidate.PrNo.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                int targetIndex = currentIndex + insertOffset;
                if (targetIndex < BatchPrEntries.Count && string.IsNullOrWhiteSpace(BatchPrEntries[targetIndex].Description) && string.IsNullOrWhiteSpace(BatchPrEntries[targetIndex].PrNo))
                {
                    BatchPrEntries[targetIndex] = candidate;
                }
                else if (targetIndex <= BatchPrEntries.Count)
                {
                    BatchPrEntries.Insert(targetIndex, candidate);
                }
                else
                {
                    BatchPrEntries.Add(candidate);
                }
                insertOffset++;
            }

            UpdateBatchEntriesSummary();
        }
    }
}
