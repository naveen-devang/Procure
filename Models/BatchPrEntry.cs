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

        public int ItemsCount => Items?.Count ?? 0;
        public decimal TotalQuantity => Items?.Sum(i => i.Quantity) ?? 0;

        public bool HasItems => Items != null && Items.Count > 0;

        public void NotifyItemsChanged()
        {
            OnPropertyChanged(nameof(ItemsCount));
            OnPropertyChanged(nameof(TotalQuantity));
            OnPropertyChanged(nameof(HasItems));
        }
    }
}
