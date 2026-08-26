using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;

namespace Procure.PageModels
{
    public partial class CallOffPageModel : ObservableObject
    {
        private readonly ICallOffRepository _repo;
        private readonly IErrorHandler _errorHandler;

        // Full flat load, kept in memory - see the artifact's scale argument (a few hundred rows,
        // total, ever). Groups is rebuilt from this whenever search text changes.
        private List<CallOffLine> _allLines = new();
        private bool _loaded;

        // Set by the page's OnAppearing/OnDisappearing. A PO change while this tab isn't the one
        // on screen just marks the cache stale (below) - the reload happens lazily on next visit
        // instead of racing a DB read against a hidden page.
        public bool IsVisible { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<MaterialGroup> Groups { get; set; } = new();

        [ObservableProperty]
        public partial string SearchText { get; set; } = string.Empty;

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

        public CallOffPageModel(ICallOffRepository repo, IErrorHandler errorHandler)
        {
            _repo = repo;
            _errorHandler = errorHandler;

            // This page model is a DI singleton that outlives every visit to the tab, so this
            // subscription is never unsubscribed - same lifetime as the event source itself.
            Utilities.PoChangeNotifier.Changed += OnPoDataChanged;
        }

        private void OnPoDataChanged()
        {
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
                _allLines = await _repo.GetAllCallOffLinesAsync();
                _loaded = true;
                RebuildGroups();

                foreach (var group in Groups)
                {
                    if (expandedNames.Contains(group.MaterialName)) group.IsExpanded = true;
                }

                if (selectedPoItemId.HasValue)
                {
                    var stillThere = Groups.SelectMany(g => g.Lines).FirstOrDefault(l => l.PoItemId == selectedPoItemId.Value);
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

        partial void OnSearchTextChanged(string value) => RebuildGroups();

        // Lowest balance % first (3B): the lines most behind on delivery surface at the top,
        // same call as the earlier design pass. Search matches material, vendor, or PO number,
        // from any starting point, in one pass over the in-memory list.
        private void RebuildGroups()
        {
            var term = SearchText?.Trim();
            IEnumerable<CallOffLine> lines = _allLines;
            if (!string.IsNullOrEmpty(term))
            {
                lines = lines.Where(l =>
                    l.MaterialName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    l.Vendor.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    l.PoNo.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            var groups = lines
                .GroupBy(l => l.MaterialName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new MaterialGroup
                {
                    MaterialName = g.Key,
                    Lines = new ObservableCollection<CallOffLine>(g.OrderBy(l => l.PercentComplete))
                })
                .OrderBy(g => g.PercentComplete)
                .ToList();

            // Auto-expand when actively searching, so a vendor-name match doesn't hide behind a
            // collapsed group; collapsed by default otherwise.
            if (!string.IsNullOrEmpty(term))
            {
                foreach (var g in groups) g.IsExpanded = true;
            }

            Groups = new ObservableCollection<MaterialGroup>(groups);
        }

        [RelayCommand]
        public void ToggleExpand(MaterialGroup group) => group.IsExpanded = !group.IsExpanded;

        [RelayCommand]
        public async Task SelectLineAsync(CallOffLine line)
        {
            if (SelectedLine != null) SelectedLine.IsSelected = false;
            SelectedLine = line;
            line.IsSelected = true;
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
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Enter a quantity greater than zero.", "OK");
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

                Groups.FirstOrDefault(g => g.Lines.Contains(SelectedLine))?.RefreshAggregates();

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

                Groups.FirstOrDefault(g => g.Lines.Contains(SelectedLine))?.RefreshAggregates();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }
    }
}
