using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    /// <summary>
    /// One material on the Raw &amp; Packing tab: a header built from a SQL aggregate, and the vendor
    /// lines behind it, which are fetched when the group is expanded and dropped when it is closed.
    ///
    /// The aggregates below come from <see cref="Summary"/> rather than from summing the lines. They
    /// used to be Lines.Sum(...), recomputed on every binding evaluation - roughly 1,920 LINQ
    /// iterations per header on a 480-line material, every time the CollectionView re-evaluated a
    /// realised row - which is what made scrolling stutter. Reading them from the summary also means
    /// a collapsed group can show correct totals while holding no lines at all.
    /// </summary>
    public partial class MaterialGroup : ObservableObject
    {
        public MaterialGroup(MaterialGroupSummary summary)
        {
            Summary = summary;
            MaterialName = summary.MaterialName;
        }

        public string MaterialName { get; }

        /// <summary>Aggregates straight from SQL. A call-off logged against a loaded line adjusts
        /// these in place (see <see cref="ApplyCalledOffDelta"/>) rather than re-querying.</summary>
        public MaterialGroupSummary Summary { get; private set; }

        /// <summary>Bound by the UI. Empty until the group is expanded, and emptied again when it is
        /// collapsed, so a closed material costs nothing but its header.</summary>
        public ObservableCollection<CallOffLine> VisibleLines { get; } = new();

        public bool LinesLoaded { get; private set; }

        /// <summary>Rows fetched per page. A material can hold hundreds of PO lines - ~480 in the
        /// 20,000-PR database - and building them all on expand cost 5.6 seconds and ~200MB. The
        /// first page is the useful one: lines come back ordered by how far behind on delivery they
        /// are, so the vendors worth chasing are the ones on screen.</summary>
        public const int PageSize = 25;

        /// <summary>Fetches one page for this group: (group, skip, take). Set by the page model.</summary>
        public Func<MaterialGroup, int, int, Task>? PageRequested { get; set; }

        public bool HasMoreLines => VisibleLines.Count < Summary.LineCount;

        public string ShowMoreText
        {
            get
            {
                var remaining = Summary.LineCount - VisibleLines.Count;
                return $"Show {Math.Min(PageSize, remaining)} more of {Summary.LineCount}";
            }
        }

        [ObservableProperty]
        public partial bool IsExpanded { get; set; }

        [ObservableProperty]
        public partial bool IsLoadingLines { get; set; }

        partial void OnIsExpandedChanged(bool value)
        {
            if (value) _ = ExpandAsync();
            else Collapse();
        }

        private async Task ExpandAsync()
        {
            if (LinesLoaded || PageRequested is null) return;
            IsLoadingLines = true;
            try { await PageRequested(this, 0, PageSize); }
            finally { IsLoadingLines = false; }
        }

        /// <summary>Appends the next page. Bound to the card's "Show more" row.</summary>
        public async Task LoadMoreAsync()
        {
            if (!IsExpanded || IsLoadingLines || !HasMoreLines || PageRequested is null) return;
            IsLoadingLines = true;
            try { await PageRequested(this, VisibleLines.Count, PageSize); }
            finally { IsLoadingLines = false; }
        }

        /// <summary>Called by the page model with a fetched page. The first page replaces whatever
        /// was there; later pages append.</summary>
        public void AddPage(IEnumerable<CallOffLine> lines, bool isFirstPage)
        {
            // Only fill a group the user still has open - an expand followed quickly by a collapse
            // must not leave rows behind a closed header.
            if (!IsExpanded) return;

            if (isFirstPage) VisibleLines.Clear();
            foreach (var line in lines) VisibleLines.Add(line);
            LinesLoaded = true;
            RefreshAggregates();
        }

        private void Collapse()
        {
            // Dropping the rows is the point: a collapsed group holds no CallOffLine objects and no
            // native views, so opening every material in turn cannot accumulate. Re-expanding costs
            // one indexed query (IX_PoItem_ItemName).
            VisibleLines.Clear();
            LinesLoaded = false;
            RefreshAggregates();
        }

        /// <summary>Keeps the header's totals right after a call-off is logged or deleted, without
        /// re-querying and without summing the loaded lines.</summary>
        public void ApplyCalledOffDelta(decimal delta)
        {
            Summary = Summary with { TotalCalledOff = Math.Max(0m, Summary.TotalCalledOff + delta) };
            RefreshAggregates();
        }

        public decimal TotalOrdered => Summary.TotalOrdered;
        public decimal TotalCalledOff => Summary.TotalCalledOff;
        public double PercentComplete => TotalOrdered <= 0 ? 0 : (double)Math.Min(1m, TotalCalledOff / TotalOrdered) * 100.0;
        public double Percent01 => PercentComplete / 100.0;
        public string VendorCountText
        {
            get
            {
                var total = Summary.LineCount == 1 ? "1 vendor" : $"{Summary.LineCount} vendors";
                // The header keeps the true total even while the body shows one page, so the cap is
                // never a lie about what exists.
                return VisibleLines.Count > 0 && VisibleLines.Count < Summary.LineCount
                    ? $"{total}, showing {VisibleLines.Count}"
                    : total;
            }
        }

        public void RefreshAggregates()
        {
            OnPropertyChanged(nameof(TotalOrdered));
            OnPropertyChanged(nameof(TotalCalledOff));
            OnPropertyChanged(nameof(PercentComplete));
            OnPropertyChanged(nameof(Percent01));
            OnPropertyChanged(nameof(VendorCountText));
            OnPropertyChanged(nameof(HasMoreLines));
            OnPropertyChanged(nameof(ShowMoreText));
        }
    }
}
