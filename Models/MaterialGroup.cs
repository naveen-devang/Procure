using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    // Client-side aggregate over CallOffLines sharing a trimmed/case-insensitive material name.
    // Built and rebuilt entirely in memory - see CallOffPageModel - there is no MaterialGroup table.
    public partial class MaterialGroup : ObservableObject
    {
        public string MaterialName { get; set; } = string.Empty;
        public ObservableCollection<CallOffLine> Lines { get; set; } = new();

        // Bound by the UI's BindableLayout instead of Lines directly. BindableLayout builds one
        // native row per item up front regardless of visibility, so a collapsed group would still
        // pay for every vendor row it never shows - this stays empty until the group is expanded
        // for the first time, then keeps its rows for any later collapse/re-expand.
        public ObservableCollection<CallOffLine> VisibleLines { get; } = new();
        private bool _visibleLinesBuilt;

        [ObservableProperty]
        public partial bool IsExpanded { get; set; }

        partial void OnIsExpandedChanged(bool value)
        {
            if (!value || _visibleLinesBuilt) return;
            _visibleLinesBuilt = true;
            foreach (var line in Lines) VisibleLines.Add(line);
        }

        public decimal TotalOrdered => Lines.Sum(l => l.OrderedQuantity);
        public decimal TotalCalledOff => Lines.Sum(l => l.CalledOffQuantity);
        public double PercentComplete => TotalOrdered <= 0 ? 0 : (double)System.Math.Min(1m, TotalCalledOff / TotalOrdered) * 100.0;
        public double Percent01 => PercentComplete / 100.0;
        public string VendorCountText => Lines.Count == 1 ? "1 vendor" : $"{Lines.Count} vendors";

        // Called after a line's CalledOffQuantity changes in place - the aggregates above are
        // computed from Lines, so nothing raises PropertyChanged for them on its own.
        public void RefreshAggregates()
        {
            OnPropertyChanged(nameof(TotalOrdered));
            OnPropertyChanged(nameof(TotalCalledOff));
            OnPropertyChanged(nameof(PercentComplete));
            OnPropertyChanged(nameof(Percent01));
        }
    }
}
