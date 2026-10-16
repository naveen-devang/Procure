using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Procure.Models;

namespace Procure.PageModels
{
    // The board's three tabs (Active / Completed / On hold & cancelled) and the batch actions that
    // move requisitions between them. A tab is just a status group handed to the same paged query the
    // board always used: switching tabs is a filter change, so nothing is read for a tab until it is
    // opened, and what it reads is a page at a time like everything else here.
    public partial class PrListPageModel
    {
        [ObservableProperty]
        public partial BoardTab SelectedTab { get; set; } = BoardTab.Active;

        /// <summary>Search without the tab limit. Set when a search finds nothing in the open tab and
        /// the user asks to look everywhere, or when a task/note sends its linked PR numbers to the
        /// board (the PR may well be in Completed). Cleared by picking a tab or clearing the search.</summary>
        [ObservableProperty]
        public partial bool SearchAllTabs { get; set; }

        [ObservableProperty]
        public partial bool CanMarkCompleted { get; set; }

        [ObservableProperty]
        public partial bool CanHoldCancel { get; set; }

        [ObservableProperty]
        public partial bool CanReopen { get; set; }

        [ObservableProperty]
        public partial string ReopenButtonText { get; set; } = "Reopen";

        /// <summary>What the status dropdown offers: only the open tab's statuses, or all of them when
        /// searching across tabs. A status from another tab would always give an empty board.</summary>
        public List<string> StatusFilterOptions => SearchAllTabs
            ? AllStatusFilterOptions
            : new List<string> { "All" }.Concat(BoardTabs.StatusesOf(SelectedTab)).ToList();

        private static readonly List<string> AllStatusFilterOptions =
            new List<string> { "All" }.Concat(ProcurementStatus.AllStatuses).ToList();

        /// <summary>The tab the board queries, or null while searching across all of them.</summary>
        private BoardTab? QueryTab => SearchAllTabs ? null : SelectedTab;

        /// <summary>True when a requisition with this status belongs on the board as it is scoped now.</summary>
        private bool InCurrentScope(string? status) => SearchAllTabs || BoardTabs.Of(status) == SelectedTab;

        /// <summary>The empty board can offer "search all tabs" only when a search is what emptied it.</summary>
        public bool CanSearchAllTabs => !SearchAllTabs && !string.IsNullOrWhiteSpace(SearchText);

        private bool _suppressScopeReload;

        /// <summary>Test seam for BoardTabsProbe: no board reload is running or waiting.</summary>
        internal bool IsReloadIdleForTest => !_reloadRunning && !_reloadQueued;

        partial void OnSelectedTabChanged(BoardTab value)
        {
            // A selection belongs to the tab it was made in; carrying it over would pull those rows
            // into a tab they do not belong to (the off-window loader keeps selected PRs loaded).
            ClearSelection();

            _suppressScopeReload = true;
            try { SearchAllTabs = false; }
            finally { _suppressScopeReload = false; }

            ReloadForScope();
        }

        partial void OnSearchAllTabsChanged(bool value)
        {
            OnPropertyChanged(nameof(CanSearchAllTabs));
            if (!_suppressScopeReload) ReloadForScope();
        }

        /// <summary>Re-reads the board for a new scope. The old rows stay on screen dimmed and inert
        /// (the search path's stale treatment) until the new ones land, so nothing from another tab can
        /// be clicked meanwhile. Resetting the status dropdown reloads on its own; otherwise reload here.</summary>
        private void ReloadForScope()
        {
            OnPropertyChanged(nameof(StatusFilterOptions));
            OnPropertyChanged(nameof(CanSearchAllTabs));
            IsSearchPending = true;
            if (SelectedStatusFilter != "All") SelectedStatusFilter = "All";
            else ApplyFilters(true);
        }

        /// <summary>The tab strip was clicked. Picking the tab that is already open still matters while
        /// searching across tabs: it returns the search to that tab.</summary>
        public void SelectTab(BoardTab tab)
        {
            if (tab != SelectedTab) SelectedTab = tab;
            else if (SearchAllTabs) SearchAllTabs = false;
        }

        /// <summary>Opens the search up to every tab (the empty board's "Search all tabs").</summary>
        [RelayCommand]
        public void SearchEveryTab() => SearchAllTabs = true;

        // ----- keeping the open tab true when a status changes underneath it -----

        private int _scopeGeneration;

        /// <summary>A requisition's status can change from many places - the detail panel adding an
        /// order, an approval landing, the edit dialog. Most already re-read the board; this is the
        /// backstop for the ones that do not, so a PR that just became Delivered does not sit in Active
        /// until the next refresh. Debounced, and it looks before it reloads.</summary>
        private void QueueScopeReconcile()
        {
            var generation = ++_scopeGeneration;
            _dispatcher.PostDelayed(TimeSpan.FromMilliseconds(1200), () =>
            {
                if (generation != _scopeGeneration) return;
                if (FilteredPrs.Any(p => !InCurrentScope(p.Status))) ApplyFilters();
            });
        }

        // ----- batch actions -----

        /// <summary>Called at the end of UpdateSelectionState: which of the tab buttons make sense for
        /// what is ticked. Merged sources are left out of every one - they only come back through
        /// Split / Unmerge, which restores their parent link as well.</summary>
        private void UpdateTabActions(List<PurchaseRequisition> selected)
        {
            var movable = selected.Where(p => !IsMergedSource(p)).ToList();
            CanMarkCompleted = movable.Any(p => BoardTabs.Of(p.Status) != BoardTab.Completed);
            CanHoldCancel = movable.Any(p => BoardTabs.Of(p.Status) == BoardTab.Active);
            CanReopen = movable.Any(p => BoardTabs.Of(p.Status) != BoardTab.Active);
            ReopenButtonText = movable.Count > 0 && movable.All(p => BoardTabs.Of(p.Status) == BoardTab.Completed)
                ? "Reopen"
                : "Resume";
        }

        private static bool IsMergedSource(PurchaseRequisition pr) =>
            string.Equals(pr.Status, ProcurementStatus.Merged, StringComparison.OrdinalIgnoreCase);

        [RelayCommand]
        public Task MarkSelectedCompletedAsync() =>
            MoveSelectedAsync(p => BoardTabs.Of(p.Status) == BoardTab.Completed ? null : ProcurementStatus.Closed, "Completed");

        [RelayCommand]
        public Task HoldSelectedAsync() =>
            MoveSelectedAsync(p => BoardTabs.Of(p.Status) == BoardTab.Active ? ProcurementStatus.OnHold : null, "On hold");

        [RelayCommand]
        public async Task CancelSelectedAsync()
        {
            var count = _loadedPrs.Count(p => p.IsSelected && !IsMergedSource(p) && BoardTabs.Of(p.Status) == BoardTab.Active);
            if (count == 0) return;

            var ok = await _dialogs.DisplayAlertAsync(
                "Cancel Requisitions",
                count == 1 ? "Cancel the selected requisition?" : $"Cancel {count} requisitions?",
                "Cancel PRs",
                "Keep");
            if (!ok) return;

            await MoveSelectedAsync(p => BoardTabs.Of(p.Status) == BoardTab.Active ? ProcurementStatus.Cancelled : null, "Cancelled");
        }

        [RelayCommand]
        public Task ReopenSelectedAsync() =>
            MoveSelectedAsync(p => BoardTabs.Of(p.Status) == BoardTab.Active ? null : InferActiveStatus(p), "Active");

        /// <summary>Where a reopened requisition belongs, read from what it has (nothing records what
        /// it was before): orders, then an approved or pending comparison, then quotes, else just raised.</summary>
        internal static string InferActiveStatus(PurchaseRequisition pr)
        {
            if (pr.Pos is { Count: > 0 })
                return pr.Pos.Any(po => po.Status == PoStatus.Delivered)
                    ? ProcurementStatus.PartiallyDelivered
                    : ProcurementStatus.PoRaised;
            if (pr.Pcr is not null)
                return pr.Pcr.IsFullyApproved ? ProcurementStatus.PcrApproved : ProcurementStatus.PcrSubmitted;
            if (pr.Rfqs is { Count: > 0 })
                return pr.Rfqs.All(r => r.IsQuoteReceived) ? ProcurementStatus.QuotesReceived : ProcurementStatus.RfqSent;
            return ProcurementStatus.PrRaised;
        }

        private bool _movingSelected;

        /// <summary>Writes a new status for each selected requisition that <paramref name="target"/>
        /// has one for, through the same save every other status edit uses (search index and material
        /// totals included), then re-reads the loaded window so the moved rows leave this tab in place.</summary>
        private async Task MoveSelectedAsync(Func<PurchaseRequisition, string?> target, string destination)
        {
            if (_movingSelected) return;   // a double click must not run the batch twice
            _movingSelected = true;
            try
            {
                var selected = _loadedPrs.Where(p => p.IsSelected).ToList();
                if (selected.Count == 0) return;

                var skippedMerged = selected.Count(IsMergedSource);
                var moved = 0;
                var failed = 0;

                foreach (var pr in selected)
                {
                    if (IsMergedSource(pr)) continue;
                    var next = target(pr);
                    if (next is null || string.Equals(next, pr.Status, StringComparison.Ordinal)) continue;

                    var before = pr.Status;
                    try
                    {
                        pr.Status = next;
                        await _prRepo.SavePrFieldsAsync(pr);
                        pr.NotifyStatusChanged();
                        pr.IsSelected = false;
                        _selectedIds.Remove(pr.Id);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        pr.Status = before;   // the row still says what the database still says
                        failed++;
                        _errorHandler.HandleError(ex);
                    }
                }

                UpdateSelectionState();
                if (moved > 0) ApplyFilters();

                var message = moved == 0
                    ? "Nothing to move"
                    : moved == 1 ? $"1 requisition moved to {destination}" : $"{moved} requisitions moved to {destination}";
                if (skippedMerged > 0) message += $" ({skippedMerged} merged skipped)";
                if (failed > 0) message += $" ({failed} failed)";
                ShowToast(message);
            }
            finally
            {
                _movingSelected = false;
            }
        }
    }
}
