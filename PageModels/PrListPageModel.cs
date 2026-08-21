using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

        // Infinite scroll. Only RevealBatchSize cards exist up front and the board grows as the user
        // scrolls, so a tab switch realises 5 cards instead of a whole page of them.
        // ponytail: revealed rows are never dropped again, so scrolling to the bottom of a long list
        // leaves every card realised and makes the next tab switch proportionally slower. Ceiling is
        // the list length; reset _revealedCount on navigate-away if that ever bites.
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
        // just parks ~430 views to be created in one synchronous block on the first tab switch -
        // which is the freeze. Hold the fill until the board is actually appearing.
        private bool _fillPending;

        [RelayCommand]
        public Task LoadPrsAsync() => LoadCoreAsync(fillUi: true);

        /// <summary>Warms the data only. The card fill waits for <see cref="ApplyPendingFill"/>.</summary>
        public Task PreloadDataAsync() => LoadCoreAsync(fillUi: false);

        /// <summary>Runs the fill a preload deferred. Cheap no-op when there is nothing pending.</summary>
        public void ApplyPendingFill()
        {
            if (!_fillPending) return;
            _fillPending = false;
            ApplyFilters();
            UpdateStatusBanner();
        }

        private async Task LoadCoreAsync(bool fillUi)
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;
                var probe = Procure.Utilities.TimingProbe.Start("LoadPrsAsync"); // ponytail-temp

                // Two independent queries, each on its own pooled SqliteConnection — overlap them
                // instead of paying for the column definitions before the PRs even start.
                var defsTask = Task.Run(() => _customColumnRepo.GetAllDefinitionsAsync());
                var prsTask = Task.Run(() => _prRepo.GetAllAsync());
                probe.Mark("queue both queries"); // ponytail-temp
                await Task.WhenAll(defsTask, prsTask).ConfigureAwait(true);
                probe.Mark("await + resume on UI"); // ponytail-temp

                // Back on the UI thread from here.
                CustomColumnDefinitions = new ObservableCollection<CustomColumnDefinition>(defsTask.Result);
                _allPrs = MergeLoadedPrs(prsTask.Result);
                probe.Mark("MergeLoadedPrs"); // ponytail-temp

                // Hide the skeleton before the cards start arriving: its shimmer animates at rate:33 on
                // this same thread and otherwise fights card inflation for the whole fill. This does let
                // a second LoadPrs slip past the IsBusy guard mid-fill — harmless, the merge is
                // idempotent and _fillGeneration retires the superseded fill.
                IsBusy = false;

                if (fillUi)
                {
                    ApplyFilters();
                    probe.Mark("ApplyFilters"); // ponytail-temp
                    UpdateStatusBanner();
                    probe.Mark("UpdateStatusBanner"); // ponytail-temp
                }
                else
                {
                    _fillPending = true;
                    probe.Mark("fill deferred (off-screen)"); // ponytail-temp
                }
                probe.Flush(); // ponytail-temp
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

        /// <summary>Folds a fresh load into the instances the board is already bound to: same Id merges
        /// in place, so an unchanged reload leaves FilteredPrs reference-identical and ApplyFilters exits
        /// on its SequenceEqual check without destroying and rebuilding a single card.</summary>
        private List<PurchaseRequisition> MergeLoadedPrs(List<PurchaseRequisition> loaded)
        {
            var live = _allPrs.ToDictionary(p => p.Id);
            var merged = new List<PurchaseRequisition>(loaded.Count);

            foreach (var fresh in loaded)
            {
                var pr = fresh;
                if (live.TryGetValue(fresh.Id, out var kept))
                {
                    kept.MergeFrom(fresh);
                    pr = kept;
                }

                // Unconditional -= then +=: idempotent for a reused instance, and it still picks up the
                // PRs that batch create and merge-master push straight into _allPrs without subscribing.
                pr.PropertyChanged -= OnPrItemPropertyChanged;
                pr.PropertyChanged += OnPrItemPropertyChanged;
                merged.Add(pr);
            }

            // Anything the DB no longer returns is dropped here — unsubscribe or the handler keeps it alive.
            var loadedIds = loaded.Select(p => p.Id).ToHashSet();
            foreach (var gone in _allPrs)
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

            // A filter that shrank the list must not leave the window stranded past the end.
            _revealedCount = Math.Max(RevealBatchSize, Math.Min(_revealedCount, TotalFilteredCount));
            var pagedList = allFiltered.Take(_revealedCount).ToList();

            // The window must never strand: it shows something whenever anything matched, never more
            // than matched, and clamping to the total is what lets RevealMore terminate.
            Debug.Assert(pagedList.Count == Math.Min(_revealedCount, TotalFilteredCount)
                         && (pagedList.Count > 0 || TotalFilteredCount == 0),
                "Infinite-scroll window stranded - the board would render empty with matches available.");

            ListSummary = TotalFilteredCount == 0
                ? "No requisitions found"
                : $"Showing {pagedList.Count} of {TotalFilteredCount} requisitions";

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
