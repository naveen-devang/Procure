using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Procure.Models
{
    /// <summary>
    /// One material on the Raw &amp; Packing tab, and the CollectionView group its vendor lines live
    /// in. Inheriting ObservableCollection is what lets a single grouped CollectionView virtualize
    /// the vendor rows as well as the headers - the rows used to sit in a BindableLayout nested
    /// inside each group, which builds every row up front no matter how many there are. At ~480
    /// lines per material that was ~4,800 native views constructed on the UI thread the moment a
    /// group was opened, which is what made expanding a card hang.
    ///
    /// Collapsing clears the collection, so a closed group costs nothing but its header, and the
    /// aggregates on show come from the SQL summary rather than from summing lines that are no
    /// longer loaded.
    /// </summary>
    public class MaterialGroup : ObservableCollection<CallOffLine>
    {
        public MaterialGroup(MaterialGroupSummary summary)
        {
            Summary = summary;
            MaterialName = summary.MaterialName;
        }

        public string MaterialName { get; }

        /// <summary>Aggregates straight from SQL. These are the truth while the group is collapsed;
        /// once lines are loaded, a call-off logged against one of them adjusts them in place
        /// (see ApplyCalledOffDelta) rather than re-querying.</summary>
        public MaterialGroupSummary Summary { get; private set; }

        /// <summary>Set by the page model; invoked when this group is expanded and its lines are not
        /// loaded. The group cannot reach the repository itself.</summary>
        public Func<MaterialGroup, Task>? LinesRequested { get; set; }

        public bool LinesLoaded { get; private set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsExpanded)));

                if (value) _ = ExpandAsync();
                else Collapse();
            }
        }

        private bool _isLoadingLines;
        public bool IsLoadingLines
        {
            get => _isLoadingLines;
            private set
            {
                if (_isLoadingLines == value) return;
                _isLoadingLines = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsLoadingLines)));
            }
        }

        private async Task ExpandAsync()
        {
            if (LinesLoaded || LinesRequested is null) return;
            IsLoadingLines = true;
            try { await LinesRequested(this); }
            finally { IsLoadingLines = false; }
        }

        /// <summary>Called by the page model once it has fetched this group's lines.</summary>
        public void SetLines(IEnumerable<CallOffLine> lines)
        {
            // Only fill a group the user still has open - an expand followed quickly by a collapse
            // must not leave hundreds of rows realised behind a closed header.
            if (!_isExpanded) return;

            Clear();
            foreach (var line in lines) Add(line);
            LinesLoaded = true;
            RefreshAggregates();
        }

        private void Collapse()
        {
            // Dropping the rows is the point: a collapsed group holds no CallOffLine objects and no
            // native views, so opening every material in turn cannot accumulate. Re-expanding costs
            // one indexed query (IX_PoItem_ItemName).
            Clear();
            LinesLoaded = false;
        }

        /// <summary>Keeps the header's totals right after a call-off is logged or deleted, without
        /// re-querying and without summing the loaded lines.</summary>
        public void ApplyCalledOffDelta(decimal delta)
        {
            Summary = Summary with { TotalCalledOff = Math.Max(0m, Summary.TotalCalledOff + delta) };
            RefreshAggregates();
        }

        // Straight from the summary - never a Sum() over Lines. Recomputing these per binding
        // evaluation cost ~1,920 LINQ iterations per header on a 480-line material, every time the
        // CollectionView re-evaluated a realised row, which is what made scrolling stutter.
        public decimal TotalOrdered => Summary.TotalOrdered;
        public decimal TotalCalledOff => Summary.TotalCalledOff;
        public double PercentComplete => TotalOrdered <= 0 ? 0 : (double)Math.Min(1m, TotalCalledOff / TotalOrdered) * 100.0;
        public double Percent01 => PercentComplete / 100.0;
        public string VendorCountText => Summary.LineCount == 1 ? "1 vendor" : $"{Summary.LineCount} vendors";

        public void RefreshAggregates()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(TotalOrdered)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(TotalCalledOff)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(PercentComplete)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Percent01)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(VendorCountText)));
        }
    }
}
