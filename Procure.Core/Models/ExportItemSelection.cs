using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class ExportItemSelection : ObservableModel
    {
        public PrItem Item { get; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        /// <summary>The Sl No. shown beside the line while editing (see Utilities.LineNumbers): the
        /// number it gets on the PCR / in Copy for email, or a dash. Display only, never saved.</summary>
        [ObservableProperty]
        public partial string LineNo { get; set; } = string.Empty;

        public string DisplaySummary => $"{Item.ItemName} • {Item.FormattedQuantity}";

        public ExportItemSelection(PrItem item, bool isSelected = true)
        {
            Item = item;
            IsSelected = isSelected;
        }
    }
}
