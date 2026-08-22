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

        [ObservableProperty]
        public partial string ToastText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsToastVisible { get; set; }

        private int _toastGeneration;

        /// <summary>In-app success pill. OS toasts (CommunityToolkit Toast) go through the packaged
        /// notification pipeline and throw 0x80070490 "Element not found" on this unpackaged app.</summary>
        public void ShowToast(string message)
        {
            ToastText = message;
            IsToastVisible = true;
            var gen = ++_toastGeneration;
            Microsoft.Maui.Dispatching.Dispatcher.GetForCurrentThread()
                ?.DispatchDelayed(TimeSpan.FromMilliseconds(2500), () =>
                {
                    if (gen == _toastGeneration) IsToastVisible = false;
                });
        }

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

        public string[] SelectableStatuses => ProcurementStatus.SelectableStatuses;
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

        /// <summary>Called from PrListPage.OnDisappearing. Nothing to tear down: the CollectionView
        /// recycles containers, so what is realised is bounded by the viewport however far the user
        /// scrolled. This used to drop the window back to one batch, which meant removing hundreds of
        /// rows one at a time - each destroying a card and relaying out the rest - and that quadratic
        /// teardown, run inside OnDisappearing, was the tab-switch freeze. The board keeps its place.</summary>
        public void BoardDisappearing() => _isBoardVisible = false;

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
        /// <param name="retain">Rows the board still shows beyond the re-read window. They are already
        /// live instances with no fresh counterpart, so they are kept subscribed and kept in
        /// <see cref="_loadedPrs"/> rather than being treated as gone.</param>
        private List<PurchaseRequisition> MergeLoadedPrs(List<PurchaseRequisition> loaded,
                                                         IReadOnlyList<PurchaseRequisition>? retain = null)
        {
            var live = _loadedPrs.ToDictionary(p => p.Id);
            var merged = new List<PurchaseRequisition>(loaded.Count + (retain?.Count ?? 0));

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

            var keptIds = merged.Select(p => p.Id).ToHashSet();

            if (retain != null)
            {
                foreach (var pr in retain)
                {
                    if (keptIds.Add(pr.Id)) merged.Add(pr);
                }
            }

            // Anything the board no longer shows is dropped here — unsubscribe or the handler keeps it
            // alive, and a long scroll would leak every row it ever loaded.
            foreach (var gone in _loadedPrs)
            {
                if (!keptIds.Contains(gone.Id)) gone.PropertyChanged -= OnPrItemPropertyChanged;
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

        [RelayCommand]
        public void ToggleFilterOverdue() => FilterOverdueOnly = !FilterOverdueOnly;

        [RelayCommand]
        public void ToggleFilterPcrPending() => FilterPcrPendingOnly = !FilterPcrPendingOnly;

        [RelayCommand]
        public void ToggleFilterUrgent() => FilterUrgentOnly = !FilterUrgentOnly;

        /// <summary>Bound to the empty state's "Reset Filters" button. Each setter triggers its own
        /// ApplyFilters pass; the generation counter retires the superseded ones.</summary>
        [RelayCommand]
        public void ResetFilters()
        {
            SearchText = string.Empty;
            SelectedStatusFilter = "All";
            FilterOverdueOnly = false;
            FilterPcrPendingOnly = false;
            FilterUrgentOnly = false;
        }

        [RelayCommand]
        public void DismissStatusMessage() => IsStatusMessageVisible = false;

        /// <summary>The board is out of date - re-read what is currently shown. Keeps its ~23 call sites
        /// and their meaning; <paramref name="resetToTop"/> is for the cases where the match set itself
        /// changed (search, status, filter chips) and the old window no longer means anything.</summary>
        private void ApplyFilters(bool resetToTop = false)
        {
            // Fire and forget: the query runs off the UI thread and the generation retires any pass a
            // later change supersedes.
            _ = ReloadWindowAsync(++_pageGeneration, resetToTop);
        }

        // Bumped on every load so a page that arrives after a newer one is discarded.
        private int _pageGeneration;
        private bool _loadingMore;
        private bool _loadMorePending;
        private int _lastVisibleIndex;

        // Matches the XAML RemainingItemsThreshold; used by the Scrolled-driven trigger below.
        private const int LoadMoreThreshold = 10;

        /// <summary>The authoritative infinite-scroll trigger. RemainingItemsThresholdReached is
        /// unreliable on Windows - a traced session showed it never firing across 30s of scrolling -
        /// so pagination keys off the Scrolled event's LastVisibleItemIndex instead. The XAML
        /// threshold stays wired as harmless redundancy behind the same re-entry guard.</summary>
        public void OnBoardScrolled(int lastVisibleItemIndex)
        {
            _lastVisibleIndex = lastVisibleItemIndex;
            if (lastVisibleItemIndex >= FilteredPrs.Count - LoadMoreThreshold) _ = LoadMoreAsync();
        }

        private bool _nearTail;

        /// <summary>Fed by the page's native ScrollViewer.ViewChanged hook - the one scroll signal
        /// that provably fires on a prewarmed page's first visit, where both MAUI scroll events stay
        /// dead until the page is left and revisited. Also fires on extent changes, so a user parked
        /// at the bottom keeps triggering as appended pages grow the list.</summary>
        public void OnBoardNearTail(bool nearTail)
        {
            _nearTail = nearTail;
            if (nearTail) _ = LoadMoreAsync();
        }

        // Rows per fetch. Larger than the old reveal batch because the CollectionView recycles
        // containers - fetching 50 rows no longer means building 50 cards.
        private const int PageSize = 50;

        // First fill only: roughly one viewport. Hydrating 50 PRs with 5 RFQs + 2 POs each costs
        // ~350ms off-thread before anything can show; 16 gets first cards on screen sooner and
        // RemainingItemsThresholdReached immediately tops the window up to PageSize and beyond.
        private const int FirstPaintPageSize = 16;

        // Bounds the re-read after an edit. Only a database cost now - the CollectionView realises a
        // screenful whatever the number is - so it is set high enough that normal use never trims the
        // board. Roughly 250ms of background materialisation at this size.
        private const int MaxWindowReread = 2000;

        /// <summary>Grows the board by one page. Bound to the CollectionView's
        /// RemainingItemsThresholdReached, which fires as the user nears the end of what is loaded. This
        /// is the whole of infinite scroll now, and it appends instead of re-reading the window.</summary>
        [RelayCommand]
        private async Task LoadMoreAsync()
        {
            Procure.Utilities.BoardTrace.Mark($"load-more-event shown={FilteredPrs.Count} total={TotalFilteredCount} inflight={_loadingMore}");
            // Triggers fire repeatedly while the tail is on screen; one that lands mid-load is
            // remembered and serviced below instead of being dropped.
            if (_loadingMore)
            {
                _loadMorePending = true;
                return;
            }
            if (FilteredPrs.Count >= TotalFilteredCount) return;

            var countBefore = FilteredPrs.Count;
            try
            {
                _loadingMore = true;
                var generation = _pageGeneration;
                var page = await Task.Run(() => _prRepo.GetPageAsync(BuildQuery(FilteredPrs.Count, PageSize)))
                                     .ConfigureAwait(true);
                if (generation != _pageGeneration) return;   // a filter change superseded this

                foreach (var pr in MergeAppend(page.Rows)) FilteredPrs.Add(pr);
                TotalFilteredCount = page.TotalCount;
                UpdateListSummary();
                Procure.Utilities.BoardTrace.Mark($"more-loaded total={FilteredPrs.Count}");
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                _loadingMore = false;
            }

            // A user parked at the tail produces no scroll delta and therefore no further trigger,
            // so keep filling until the window is ahead of the viewport. The grew-this-pass check
            // keeps a non-appending pass from chaining forever.
            var pending = _loadMorePending;
            _loadMorePending = false;
            if ((pending || _nearTail || _lastVisibleIndex >= FilteredPrs.Count - LoadMoreThreshold)
                && FilteredPrs.Count > countBefore
                && FilteredPrs.Count < TotalFilteredCount)
            {
                _ = LoadMoreAsync();
            }
        }

        private PrQuery BuildQuery(int skip, int take) => new(
            Search: SearchText,
            Status: SelectedStatusFilter,
            OverdueOnly: FilterOverdueOnly,
            PcrPendingOnly: FilterPcrPendingOnly,
            UrgentOnly: FilterUrgentOnly,
            NormalOverdueDays: _settingsService.NormalOverdueDays,
            UrgentOverdueDays: _settingsService.UrgentOverdueDays,
            Skip: skip,
            Take: take);

        private void UpdateListSummary() =>
            ListSummary = TotalFilteredCount == 0
                ? "No requisitions found"
                : $"Showing {FilteredPrs.Count} of {TotalFilteredCount} requisitions";

        /// <summary>Merge for appended rows: reuses any instance already loaded, subscribes the rest, and
        /// drops nothing - appending never removes anything from the board.</summary>
        private List<PurchaseRequisition> MergeAppend(List<PurchaseRequisition> loaded)
        {
            var live = _loadedPrs.ToDictionary(p => p.Id);
            var merged = new List<PurchaseRequisition>(loaded.Count);

            foreach (var fresh in loaded)
            {
                var pr = fresh;
                if (live.TryGetValue(fresh.Id, out var kept)) { kept.MergeFrom(fresh); pr = kept; }
                else _loadedPrs.Add(pr);

                pr.PropertyChanged -= OnPrItemPropertyChanged;
                pr.PropertyChanged += OnPrItemPropertyChanged;
                pr.IsSelected = _selectedIds.Contains(pr.Id);
                merged.Add(pr);
            }

            return merged;
        }

        /// <summary>Re-reads the loaded window and reconciles it in place, so an edit does not throw the
        /// user back to the top. Only edits and filter changes reach this - scrolling appends through
        /// LoadMoreAsync, which never re-reads what it already has.</summary>
        private async Task ReloadWindowAsync(int generation, bool resetToTop)
        {
            // The skeleton covers the window where the board has nothing to show and a query is still
            // running. Without it the empty view - "No requisitions found" - flashes before the first
            // page lands, which now happens on every open because the read is asynchronous.
            var showSkeleton = FilteredPrs.Count == 0;
            if (showSkeleton)
            {
                IsBusy = true;
                Procure.Utilities.BoardTrace.Mark("skeleton-built");
            }

            try
            {
                // Re-read exactly what is on the board, so an edit leaves the user where they were.
                // Bounded, because someone who scrolled to row 500 should not pay for that on every save.
                var take = showSkeleton
                    ? FirstPaintPageSize
                    : resetToTop
                        ? PageSize
                        : Math.Min(FilteredPrs.Count, MaxWindowReread);

                var page = await Task.Run(() => _prRepo.GetPageAsync(BuildQuery(0, take))).ConfigureAwait(true);
                if (showSkeleton) Procure.Utilities.BoardTrace.Mark($"query-done rows={page.Rows.Count}");

                if (showSkeleton)
                {
                    // Give the page shell and the shimmering skeleton a painted frame before card
                    // realization takes the UI thread - without this yield the entire open, from
                    // click to first card pixels, is one continuous freeze. Same calibration as
                    // LazyExpander's placeholder delay.
                    await Task.Delay(80).ConfigureAwait(true);
                    if (generation != _pageGeneration) return;
                }

                // A selected PR outside the window still has to be loaded, or the action bar and the
                // batch commands would silently act on only the part of the selection still on screen.
                // Selections are a handful of rows, so this is bounded and usually skipped entirely.
                var offWindow = _selectedIds.Except(page.Rows.Select(r => r.Id)).ToList();
                var extra = offWindow.Count == 0
                    ? new List<PurchaseRequisition>()
                    : await Task.Run(() => _prRepo.GetByIdsAsync(offWindow)).ConfigureAwait(true);

                // Back on the UI thread. A newer filter has already queued its own pass.
                if (generation != _pageGeneration) return;

                // Anything the user had scrolled past the re-read window keeps its place. Re-reading all
                // of it would cost seconds at 12,000 rows, and dropping it - which is what the clamp used
                // to do - yanks the board back to the end of the window mid-edit.
                var tail = resetToTop
                    ? new List<PurchaseRequisition>()
                    : FilteredPrs.Skip(page.Rows.Count).ToList();

                // Page rows first, so the visible window is the head of the merged list.
                _loadedPrs = MergeLoadedPrs(page.Rows.Concat(extra).ToList(), retain: tail);

                var rows = _loadedPrs.Take(page.Rows.Count).ToList();
                var reread = rows.Select(r => r.Id).ToHashSet();
                rows.AddRange(tail.Where(p => !reread.Contains(p.Id)));

                TotalFilteredCount = page.TotalCount;

                // Selection state travels with the ids, so the action bar has to be recomputed once the
                // page it describes has actually landed.
                UpdateSelectionState();
                ReplaceRows(rows, resetToTop);
                UpdateListSummary();
                if (showSkeleton)
                {
                    Procure.Utilities.BoardTrace.Mark($"rows-filled n={rows.Count}");
                    // The first fill is one viewport; grow the window to a full page shortly after,
                    // off the critical path, so "Showing N of M" reaches PageSize without the user
                    // having to scroll to trigger the first threshold fetch.
                    Microsoft.Maui.Dispatching.Dispatcher.GetForCurrentThread()
                        ?.DispatchDelayed(TimeSpan.FromMilliseconds(250), () => _ = LoadMoreAsync());
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


        /// <summary>Reconciles the bound collection to <paramref name="rows"/>. Rows that are already
        /// there keep their position, so an edit does not disturb the cards around it.
        ///
        /// This used to have to trickle inserts across dispatcher ticks, because every insert built a
        /// whole card synchronously and every removal tore one down - which is what froze the app when
        /// a few hundred rows changed at once. The CollectionView only realises what is on screen, so a
        /// wholesale swap now costs a screenful of containers however many rows moved.</summary>
        private void ReplaceRows(List<PurchaseRequisition> rows, bool reset)
        {
            if (FilteredPrs.SequenceEqual(rows)) return;

            // Only a genuinely new match set is expressed as a reset. Deciding this on size instead
            // would send the board back to the top every time a single row dropped out of a long
            // window - a status edit at row 400 would look like the list had been rebuilt.
            if (reset)
            {
                FilteredPrs.Clear();
                foreach (var pr in rows) FilteredPrs.Add(pr);
                return;
            }

            // Set lookup: rows can be up to MaxWindowReread deep, and List.Contains per element made
            // this loop O(n^2) on the dispatcher thread.
            var rowSet = new HashSet<PurchaseRequisition>(rows);
            for (var i = FilteredPrs.Count - 1; i >= 0; i--)
            {
                if (!rowSet.Contains(FilteredPrs[i])) FilteredPrs.RemoveAt(i);
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var existing = FilteredPrs.IndexOf(rows[i]);
                if (existing < 0) FilteredPrs.Insert(i, rows[i]);
                else if (existing != i) FilteredPrs.Move(existing, i);
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
                ProcurementStatus.SelectableStatuses);

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

        /// <summary>Esc support: closes the topmost open modal overlay through its Close command
        /// (which may revert state, e.g. the edit modal's snapshot restore). False when none is open.</summary>
        public bool CloseTopmostModal()
        {
            // Config/export modals first - they open on top of the board's other overlays.
            if (IsApprovalConfigModalVisible) { CloseApprovalConfigModal(); return true; }
            if (IsExportPcrModalVisible) { CloseExportPcrModal(); return true; }
            if (IsEditModalVisible) { CloseEditModal(); return true; }
            if (IsAddRfqModalVisible) { CloseAddRfqModal(); return true; }
            if (IsAddPoModalVisible) { CloseAddPoModal(); return true; }
            if (IsMergePrModalVisible) { CloseMergePrModal(); return true; }
            if (IsSplitPrModalVisible) { CloseSplitPrModal(); return true; }
            if (IsBatchRfqModalVisible) { CloseBatchRfqModal(); return true; }
            if (IsBatchPoModalVisible) { CloseBatchPoModal(); return true; }
            if (IsBatchCreateModalVisible) { CloseBatchCreateModal(); return true; }
            return false;
        }

    }
}
