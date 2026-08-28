using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class BatchPrEntry : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [ObservableProperty]
        public partial string PrNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Description { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Requestor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Plant { get; set; } = ProcurementPlant.RW01;

        [ObservableProperty]
        public partial string PrType { get; set; } = ProcurementPrType.StoresAndSpares;

        [ObservableProperty]
        public partial string Priority { get; set; } = ProcurementPriority.Normal;

        [ObservableProperty]
        public partial string Status { get; set; } = ProcurementStatus.PrRaised;

        [ObservableProperty]
        public partial string Notes { get; set; } = string.Empty;

        [ObservableProperty]
        public partial ObservableCollection<PrItem> Items { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<CustomFieldValue> CustomValues { get; set; } = new();

        // 1-based position within BatchPrEntries, for the "PR 1 / PR 2 / ..." row badge shown
        // once there's more than one row. Kept current from PrListPageModel.BatchCreate.cs's
        // UpdateBatchEntriesSummary, the single place every add/remove/paste already funnels through.
        [ObservableProperty]
        public partial int DisplayIndex { get; set; } = 1;

        public int ItemsCount => Items?.Count ?? 0;
        public decimal TotalQuantity => Items?.Sum(i => i.Quantity) ?? 0;

        public bool HasItems => Items != null && Items.Count > 0;

        // Whether any custom field has a value on this row specifically - drives the list's
        // "customized" dot. Deliberately not a diff against the shared defaults (that would need
        // this model to know about PrListPageModel's BatchShared* state); "has a tag set at all"
        // is what the dot needs to mean, and it's cheap to keep current.
        public bool HasCustomizations => CustomValues != null && CustomValues.Any(v => v.HasValue);

        public void NotifyItemsChanged()
        {
            OnPropertyChanged(nameof(ItemsCount));
            OnPropertyChanged(nameof(TotalQuantity));
            OnPropertyChanged(nameof(HasItems));
        }

        // CustomValues is only ever assigned once (at row creation) with a fixed set of columns,
        // so wiring per-value change notifications here - rather than re-subscribing on every
        // collection mutation - is enough to keep HasCustomizations current as values are typed.
        partial void OnCustomValuesChanged(ObservableCollection<CustomFieldValue> value)
        {
            foreach (var cv in value)
            {
                cv.PropertyChanged += (_, __) => OnPropertyChanged(nameof(HasCustomizations));
            }
        }
    }
}
