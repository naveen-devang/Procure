using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

        // The PRs currently loaded - one page, not the table. Everything else lives in SQLite and is
        // reached through _prRepo. Loading the lot cost 3.1s and 231MB at 20,000 PRs; a page costs ~10ms.
        private List<PurchaseRequisition> _loadedPrs = new();

        /// <summary>The PRs the board has in memory right now. Only ever the current page - use
        /// <see cref="GetSelectedPrsAsync"/> or a repository query for anything wider.</summary>
        public IReadOnlyList<PurchaseRequisition> LoadedPrs => _loadedPrs;

        /// <summary>Selection has to outlive the page it was made on: a checked PR that scrolls out of
        /// the window is evicted from memory, so the ids are the record and PurchaseRequisition.IsSelected
        /// is just the checkbox binding, restored from here whenever a page loads.</summary>
        private readonly HashSet<Guid> _selectedIds = new();

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

        // Infinite scroll. Only RevealBatchSize cards exist up front and the board grows as the user
        // scrolls, so a tab switch realises 5 cards instead of a whole page of them. BoardDisappearing
        // drops the window back to one batch, so a session that scrolled deep does not leave every card
        // it ever revealed for the next tab switch to rebuild.
        private const int RevealBatchSize = 5;
        private int _revealedCount = RevealBatchSize;

        [ObservableProperty]
        public partial int TotalFilteredCount { get; set; }

        [ObservableProperty]
        public partial string ListSummary { get; set; } = "Showing 0 requisitions";


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

        // Set when a load finished while the board was still off-screen. Shell does not realise a
        // page's native controls until you navigate to it, so filling FilteredPrs during the preload
        // just parks the cards to be created in one synchronous block on the first tab switch -
        // which is the freeze. Hold the fill until the board is actually appearing.
        private bool _fillPending;
        private bool _isBoardVisible;
        private bool _hasLoadedOnce;

        [RelayCommand]
        public Task LoadPrsAsync() => LoadCoreAsync(fillUi: true);

        /// <summary>Warms the data before the board's XAML has ever been built. The card fill waits for
        /// <see cref="BoardAppearing"/>, unless the user reaches the board first - see LoadCoreAsync.</summary>
        public Task PreloadDataAsync() => LoadCoreAsync(fillUi: false);

        /// <summary>Called from PrListPage.OnAppearing: releases a fill the preload deferred, or starts
        /// the load outright if no preload ever ran.</summary>
        public void BoardAppearing()
        {
            _isBoardVisible = true;

            if (_fillPending)
            {
                _fillPending = false;
                ApplyFilters();
                UpdateStatusBanner();
            }
            else if (!_hasLoadedOnce)
            {
                // Also the path when a preload is still in flight: LoadCoreAsync returns early on
                // IsBusy, and the in-flight load now sees _isBoardVisible and fills itself.
                _ = LoadPrsAsync();
            }
        }

        /// <summary>Called from PrListPage.OnDisappearing. Drops the infinite-scroll window back to one
        /// batch, so a session that scrolled to the bottom does not leave every card it ever revealed
        /// alive for every later tab switch to pay for. Guarded: a user who never scrolled pays nothing.</summary>
        public void BoardDisappearing()
        {
            _isBoardVisible = false;
            if (_revealedCount > RevealBatchSize) ApplyFilters(resetReveal: true);
        }

        private bool _loadInFlight;

        private async Task LoadCoreAsync(bool fillUi)
        {
            if (_loadInFlight) return;

            try
            {
                _loadInFlight = true;

                // The column definitions are the only thing a load still needs up front; the PR rows
                // themselves arrive through ReloadPageAsync, which reads one page rather than the table.
                CustomColumnDefinitions = new ObservableCollection<CustomColumnDefinition>(
                    await Task.Run(() => _customColumnRepo.GetAllDefinitionsAsync()).ConfigureAwait(true));
                _hasLoadedOnce = true;

                // _isBoardVisible covers the race where the user opens the board while a preload is
                // still running: without it the load would defer a fill nobody is left to release.
                if (fillUi || _isBoardVisible)
                {
                    _fillPending = false;
                    ApplyFilters();
                    UpdateStatusBanner();
                }
                else
                {
                    _fillPending = true;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                _loadInFlight = false;
            }
        }

        /// <summary>Folds a freshly read page into the instances the board is already bound to: same Id
        /// merges in place, so a reload that changed nothing leaves FilteredPrs reference-identical and
        /// the caller's SequenceEqual check exits without rebuilding a single card. Also reapplies the
        /// checkbox state from <see cref="_selectedIds"/>, which is what lets a selection survive being
        /// scrolled or filtered out of the loaded window.</summary>
        private List<PurchaseRequisition> MergeLoadedPrs(List<PurchaseRequisition> loaded)
        {
            var live = _loadedPrs.ToDictionary(p => p.Id);
            var merged = new List<PurchaseRequisition>(loaded.Count);

            foreach (var fresh in loaded)
            {
                var pr = fresh;
                if (live.TryGetValue(fresh.Id, out var kept))
                {
                    kept.MergeFrom(fresh);
                    pr = kept;
                }

                // Unconditional -= then +=: idempotent for a reused instance, and it still picks up PRs
                // that arrive by any path other than a merge.
                pr.PropertyChanged -= OnPrItemPropertyChanged;
                pr.PropertyChanged += OnPrItemPropertyChanged;
                pr.IsSelected = _selectedIds.Contains(pr.Id);
                merged.Add(pr);
            }

            // Anything outside the new window is dropped here — unsubscribe or the handler keeps it alive.
            var loadedIds = loaded.Select(p => p.Id).ToHashSet();
            foreach (var gone in _loadedPrs)
            {
                if (!loadedIds.Contains(gone.Id)) gone.PropertyChanged -= OnPrItemPropertyChanged;
            }

            Debug.Assert(merged.All(p => !live.TryGetValue(p.Id, out var before) || ReferenceEquals(p, before)),
                "A reload replaced a live PurchaseRequisition instead of merging into it - every bound card will be rebuilt.");

            return merged;
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

        /// <summary>Grows the visible list by one batch. The scroll handler calls this as the user
        /// nears the bottom; a no-op once everything that passes the filter is already on screen.</summary>
        public void RevealMore()
        {
            if (_revealedCount >= TotalFilteredCount) return;
            _revealedCount += RevealBatchSize;
            ApplyFilters();
        }

        [RelayCommand]
        public void ToggleFilterOverdue() => FilterOverdueOnly = !FilterOverdueOnly;

        [RelayCommand]
        public void ToggleFilterPcrPending() => FilterPcrPendingOnly = !FilterPcrPendingOnly;

        [RelayCommand]
        public void ToggleFilterUrgent() => FilterUrgentOnly = !FilterUrgentOnly;

        [RelayCommand]
        public void DismissStatusMessage() => IsStatusMessageVisible = false;

        private void ApplyFilters(bool resetReveal = false)
        {
            if (resetReveal)
            {
                _revealedCount = RevealBatchSize;
            }

            // Fire and forget: the query runs off the UI thread and the generation retires any pass a
            // later filter change supersedes. Kept synchronous so the ~15 places that call this after a
            // save still read as "the board is now out of date, refresh it".
            _ = ReloadPageAsync(++_pageGeneration);
        }

        // Bumped on every ApplyFilters so a page that arrives after a newer filter is discarded.
        private int _pageGeneration;

        /// <summary>Reads the current window from SQLite and reconciles it into the bound collection.
        /// Every filter the board offers is applied in the WHERE clause, so this costs the same whether
        /// the table holds 50 rows or 20,000.</summary>
        private async Task ReloadPageAsync(int generation)
        {
            // The skeleton covers the window where the board has nothing to show and a query is still
            // running. Without it the empty view - "No requisitions found" - flashes before the first
            // page lands, which now happens on every open because the read is asynchronous.
            var showSkeleton = FilteredPrs.Count == 0;
            if (showSkeleton) IsBusy = true;

            try
            {
                var query = new PrQuery(
                    Search: SearchText,
                    Status: SelectedStatusFilter,
                    OverdueOnly: FilterOverdueOnly,
                    PcrPendingOnly: FilterPcrPendingOnly,
                    UrgentOnly: FilterUrgentOnly,
                    NormalOverdueDays: _settingsService.NormalOverdueDays,
                    UrgentOverdueDays: _settingsService.UrgentOverdueDays,
                    Skip: 0,
                    Take: _revealedCount);

                var page = await Task.Run(() => _prRepo.GetPageAsync(query)).ConfigureAwait(true);

                // A selected PR outside the window still has to be loaded, or the action bar and the
                // batch commands would silently act on only the part of the selection still on screen.
                // Selections are a handful of rows, so this is bounded and usually skipped entirely.
                var offWindow = _selectedIds.Except(page.Rows.Select(r => r.Id)).ToList();
                var extra = offWindow.Count == 0
                    ? new List<PurchaseRequisition>()
                    : await Task.Run(() => _prRepo.GetByIdsAsync(offWindow)).ConfigureAwait(true);

                // Back on the UI thread. A newer filter has already queued its own pass.
                if (generation != _pageGeneration) return;

                // Page rows first, so the visible window is the head of the merged list.
                _loadedPrs = MergeLoadedPrs(page.Rows.Concat(extra).ToList());
                var pagedList = _loadedPrs.Take(page.Rows.Count).ToList();
                TotalFilteredCount = page.TotalCount;

                // A filter that shrank the list must not leave the window stranded past the end - and
                // without this, clearing a narrow filter after a long scroll would ask for the whole
                // list in one query.
                _revealedCount = Math.Max(RevealBatchSize, Math.Min(_revealedCount, TotalFilteredCount));

                // The window must never strand: it shows something whenever anything matched, and never
                // more than matched - that clamp is what lets RevealMore terminate.
                Debug.Assert(pagedList.Count == Math.Min(_revealedCount, TotalFilteredCount),
                    "Infinite-scroll window stranded - the board would render empty with matches available.");

                // Selection state travels with the ids, so the action bar has to be recomputed once the
                // page it describes has actually landed.
                UpdateSelectionState();

                ListSummary = TotalFilteredCount == 0
                    ? "No requisitions found"
                    : $"Showing {pagedList.Count} of {TotalFilteredCount} requisitions";

                // Reconcile the existing collection instead of replacing the instance: replacing it makes
                // the bound BindableLayout tear down and rebuild every card, which also wipes each card's
                // expanded state.
                // ponytail: O(n^2) diff via IndexOf - fine while the window is a page; swap in an index
                // map if the reveal window ever runs into the hundreds.
                if (!FilteredPrs.SequenceEqual(pagedList))
                {
                    for (var i = FilteredPrs.Count - 1; i >= 0; i--)
                    {
                        if (!pagedList.Contains(FilteredPrs[i]))
                            FilteredPrs.RemoveAt(i);
                    }

                    // Each insert makes the bound layout build a whole card synchronously, so filling a
                    // window in one pass freezes the UI. Place the first rows now and let the rest arrive
                    // over following ticks: the board paints almost immediately and completes behind you.
                    FillPage(pagedList, 0, ++_fillGeneration);
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                // Only the pass that raised it clears it, or a superseded query would uncover an empty
                // board while the current one is still running.
                if (showSkeleton && generation == _pageGeneration) IsBusy = false;
            }
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

        /// <summary>The banner counts every PR, not just the loaded page, so they come from SQL. Fire and
        /// forget for the same reason ApplyFilters is: callers treat it as "the board changed".</summary>
        private void UpdateStatusBanner() => _ = UpdateStatusBannerAsync();

        private async Task UpdateStatusBannerAsync()
        {
            try
            {
                var normalDays = _settingsService.NormalOverdueDays;
                var urgentDays = _settingsService.UrgentOverdueDays;

                var (overdue, pcrPending) = await Task
                    .Run(() => _prRepo.GetBannerCountsAsync(normalDays, urgentDays))
                    .ConfigureAwait(true);

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
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
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

                var parentPr = FilteredPrs.FirstOrDefault(p => p.Id == po.PrId);
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

                var parentPr = FilteredPrs.FirstOrDefault(p => p.Id == rfq.PrId);
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
