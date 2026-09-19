using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class ExportItemSelection : ObservableModel
    {
        public PrItem Item { get; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        public string DisplaySummary => $"{Item.ItemName} • {Item.FormattedQuantity}";

        public ExportItemSelection(PrItem item, bool isSelected = true)
        {
            Item = item;
            IsSelected = isSelected;
        }
    }
}
