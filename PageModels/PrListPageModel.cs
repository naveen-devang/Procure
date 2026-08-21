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
    // Board state and the paths every feature file leans on: construction and disposal,
    // loading, filtering, paging, the status banner and inline status transitions.
    // Feature areas live in the sibling PrListPageModel.*.cs partials.
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

    }
}
