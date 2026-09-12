using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Procure.Abstractions;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;

namespace Procure.PageModels
{
    public partial class CallOffPageModel : ObservableObject
    {
        private readonly ICallOffRepository _repo;
        private readonly IErrorHandler _errorHandler;

        // Only the collapsed material rows load; a group's lines arrive when it is expanded, and
        // go again when it is closed. This used to be a full flat load of every eligible PO item,
        // kept for the app's lifetime: 47,976 rows on a 20,000-PR database, 506ms of query plus the
        // object graph, none of it released on leaving the tab.
        private bool _loaded;
        private Guid _selectedGroupKey;
        private MaterialGroup? _selectedGroup;

        // Set by the page's OnAppearing/OnDisappearing. A PO change while this tab isn't the one
        // on screen just marks the cache stale (below) - the reload happens lazily on next visit
        // instead of racing a DB read against a hidden page.
        public bool IsVisible { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<MaterialGroup> Groups { get; set; } = new();

        [ObservableProperty]
        public partial string SearchText { get; set; } = string.Empty;

        /// <summary>Newest PO first by default. Toggled from the button beside Refresh.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SortOrderLabel))]
        [NotifyPropertyChangedFor(nameof(SortOrderGlyph))]
        public partial bool SortNewestFirst { get; set; } = true;

        public string SortOrderLabel => SortNewestFirst ? "Newest first" : "Oldest first";
        public string SortOrderGlyph => SortNewestFirst ? "" : "";   // sort down / up

        [RelayCommand]
        public async Task ToggleSortOrderAsync()
        {
            SortNewestFirst = !SortNewestFirst;
            await LoadAsync(force: true);
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NoLineSelected))]
        public partial CallOffLine? SelectedLine { get; set; }

        public bool NoLineSelected => SelectedLine is null;

        [ObservableProperty]
        public partial ObservableCollection<PoItemCallOff> SelectedLineHistory { get; set; } = new();

        // Unsorted source for the currently selected line's history; SelectedLineHistory is always
        // a sorted projection of this, rebuilt by ApplyHistorySort rather than re-queried.
        private List<PoItemCallOff> _currentHistory = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DateSortGlyph))]
        [NotifyPropertyChangedFor(nameof(QuantitySortGlyph))]
        public partial string HistorySortColumn { get; set; } = "Date";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DateSortGlyph))]
        [NotifyPropertyChangedFor(nameof(QuantitySortGlyph))]
        public partial bool HistorySortAscending { get; set; }

        public string DateSortGlyph => HistorySortColumn == "Date" ? (HistorySortAscending ? "" : "") : string.Empty;
        public string QuantitySortGlyph => HistorySortColumn == "Quantity" ? (HistorySortAscending ? "" : "") : string.Empty;

        [ObservableProperty]
        public partial DateTime NewCallOffDate { get; set; } = DateTime.Today;

        [ObservableProperty]
        public partial string NewCallOffQuantity { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewCallOffNote { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        private readonly IUiDispatcher _dispatcher;
        private readonly IDialogService _dialogs;

        public CallOffPageModel(ICallOffRepository repo, IErrorHandler errorHandler,
            IUiDispatcher dispatcher, IDialogService dialogs)
        {
            _repo = repo;
            _errorHandler = errorHandler;
            _dispatcher = dispatcher;
            _dialogs = dialogs;

            // This page model is a DI singleton that outlives every visit to the tab, so this
            // subscription is never unsubscribed - same lifetime as the event source itself.
            Utilities.DataChangeNotifier.Changed += OnDataChanged;
        }

        private void OnDataChanged(Utilities.ProcurementChange what)
        {
            // PO writes move what this tab reads, and so do PR writes: the query is filtered by
            // pr.PrType, so switching a requisition to or from Raw / Packing Material adds or
            // removes its PO items here. That was missed, and since this page model is a DI
            // singleton the tab stayed stale for the rest of the session - change a PR's type and
            // it never appeared. A quote edit still costs nothing.
            const Utilities.ProcurementChange Watched = Utilities.ProcurementChange.Po | Utilities.ProcurementChange.Pr;
            if ((what & Watched) == 0) return;

            _loaded = false;
            if (IsVisible) _ = LoadAsync(force: true);
        }

        [RelayCommand]
        public Task RefreshAsync() => LoadAsync(force: true);

        public async Task LoadAsync(bool force = false)
        {
            if (IsBusy) return;
            if (_loaded && !force) return;

            var expandedNames = Groups.Where(g => g.IsExpanded).Select(g => g.MaterialName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedPoItemId = SelectedLine?.PoItemId;

            try
            {
                IsBusy = true;
                await RebuildGroupsAsync();
                _loaded = true;

                // Only the groups the user actually had open are refilled.
                foreach (var group in Groups)
                {
                    if (expandedNames.Contains(group.MaterialName)) group.IsExpanded = true;
                }

                if (selectedPoItemId.HasValue)
                {
                    await Task.WhenAll(Groups.Where(g => g.IsExpanded && !g.LinesLoaded)
                        .Select(g => LoadGroupPageAsync(g, 0, MaterialGroup.PageSize)));
                    var stillThere = Groups.SelectMany(g => g.VisibleLines).FirstOrDefault(l => l.PoItemId == selectedPoItemId.Value);
                    if (stillThere != null) await SelectLineAsync(stillThere);
                    else
                    {
                        SelectedLine = null;
                        SelectedLineHistory.Clear();
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

        /// <summary>Called from the page's OnDisappearing. Every expanded group drops its rows, so
        /// leaving the tab cannot leave thousands of CallOffLine objects and their native views
        /// resident - the old full load was never released at all.</summary>
        public void ReleaseLines()
        {
            foreach (var group in Groups) group.IsExpanded = false;
        }

        private int _searchGeneration;

        // Debounce, matching the PR board. Every keystroke used to re-filter every line, re-group
        // them, allocate an ObservableCollection per group and then replace Groups outright - which
        // rebuilds the whole CollectionView. Generation counter rather than a CancellationTokenSource:
        // this already runs on the UI thread, so a superseded pass just fails the check and returns.
        partial void OnSearchTextChanged(string value)
        {
            var generation = ++_searchGeneration;
            _dispatcher.PostDelayed(TimeSpan.FromMilliseconds(300), async () =>
            {
                if (generation != _searchGeneration) return;
                await RebuildGroupsAsync(generation);
            });
        }

        // Search matches material, vendor or PO number, applied in SQL. Groups stay collapsed:
        // auto-expanding every match used to build every matching material's full vendor list at
        // once - on a common term that was ~48,000 rows in one pass, which never finished.
        private async Task RebuildGroupsAsync(int? generation = null)
        {
            var term = SearchText?.Trim();
            try
            {
                var summaries = await _repo.GetMaterialSummariesAsync(term).ConfigureAwait(true);
                if (generation.HasValue && generation.Value != _searchGeneration) return;

                // Ordered by the newest PO the material appears on, not by how complete it is:
                // sorting by PercentComplete meant logging a delivery moved the row you had just
                // logged against out from under you.
                var ordered = SortNewestFirst
                    ? summaries.OrderByDescending(s => s.LastActivity, StringComparer.Ordinal).ThenBy(s => s.MaterialName, StringComparer.OrdinalIgnoreCase)
                    : summaries.OrderBy(s => s.LastActivity, StringComparer.Ordinal).ThenBy(s => s.MaterialName, StringComparer.OrdinalIgnoreCase);

                var groups = ordered
                    .Select(s => new MaterialGroup(s) { PageRequested = LoadGroupPageAsync })
                    .ToList();

                Groups = new ObservableCollection<MaterialGroup>(groups);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // The one place a group's lines are fetched, one page at a time. Guarded so an expand
        // arriving while a page is already in flight does not issue a second query for it.
        private async Task LoadGroupPageAsync(MaterialGroup group, int skip, int take)
        {
            if (skip == 0 && group.LinesLoaded) return;
            try
            {
                var lines = await _repo
                    .GetLinesForMaterialAsync(group.MaterialName, SearchText?.Trim(), skip, take)
                    .ConfigureAwait(true);
                group.AddPage(lines, isFirstPage: skip == 0);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        /// <summary>Bound to the card's "Show more" row.</summary>
        [RelayCommand]
        public Task LoadMoreLinesAsync(MaterialGroup? group) => group?.LoadMoreAsync() ?? Task.CompletedTask;

        [RelayCommand]
        public void ToggleExpand(MaterialGroup group) => group.IsExpanded = !group.IsExpanded;

        [RelayCommand]
        public async Task SelectLineAsync(CallOffLine line)
        {
            if (SelectedLine != null) SelectedLine.IsSelected = false;
            SelectedLine = line;
            line.IsSelected = true;
            // Remembered here so logging a call-off does not scan every group's lines to find the
            // one that owns the selection.
            _selectedGroupKey = line.PoItemId;
            _selectedGroup = Groups.FirstOrDefault(g => g.VisibleLines.Contains(line));
            NewCallOffDate = DateTime.Today;
            NewCallOffQuantity = string.Empty;
            NewCallOffNote = string.Empty;

            try
            {
                _currentHistory = await _repo.GetHistoryAsync(line.PoItemId);
                ApplyHistorySort();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void SortHistory(string column)
        {
            if (HistorySortColumn == column) HistorySortAscending = !HistorySortAscending;
            else
            {
                HistorySortColumn = column;
                HistorySortAscending = false;
            }
            ApplyHistorySort();
        }

        private void ApplyHistorySort()
        {
            IEnumerable<PoItemCallOff> sorted = HistorySortColumn == "Quantity"
                ? (HistorySortAscending ? _currentHistory.OrderBy(h => h.Quantity) : _currentHistory.OrderByDescending(h => h.Quantity))
                : (HistorySortAscending ? _currentHistory.OrderBy(h => h.CallOffDate) : _currentHistory.OrderByDescending(h => h.CallOffDate));

            SelectedLineHistory = new ObservableCollection<PoItemCallOff>(sorted);
        }

        [RelayCommand]
        public async Task LogCallOffAsync()
        {
            if (SelectedLine is null) return;

            if (!decimal.TryParse(NewCallOffQuantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
            {
                await _dialogs.DisplayAlertAsync("Validation", "Enter a quantity greater than zero.", "OK");
                return;
            }

            var entry = new PoItemCallOff
            {
                PoItemId = SelectedLine.PoItemId,
                CallOffDate = NewCallOffDate,
                Quantity = qty,
                Note = string.IsNullOrWhiteSpace(NewCallOffNote) ? null : NewCallOffNote.Trim()
            };

            try
            {
                await _repo.LogCallOffAsync(entry);
                _currentHistory.Add(entry);
                ApplyHistorySort();
                SelectedLine.CalledOffQuantity += qty;

                _selectedGroup?.ApplyCalledOffDelta(qty);

                NewCallOffQuantity = string.Empty;
                NewCallOffNote = string.Empty;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task DeleteCallOffAsync(PoItemCallOff entry)
        {
            if (SelectedLine is null) return;

            try
            {
                await _repo.DeleteCallOffAsync(entry.Id);
                _currentHistory.Remove(entry);
                SelectedLineHistory.Remove(entry);
                SelectedLine.CalledOffQuantity = Math.Max(0, SelectedLine.CalledOffQuantity - entry.Quantity);

                _selectedGroup?.ApplyCalledOffDelta(-entry.Quantity);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }
    }
}
