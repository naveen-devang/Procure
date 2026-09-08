using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class SplitPrEntry : ObservableObject
    {
        public PurchaseRequisition Pr { get; set; } = null!;

        public string PrNo => Pr?.PrNo ?? string.Empty;
        public string Requestor => Pr?.Requestor ?? string.Empty;
        public string Plant => Pr?.Plant ?? string.Empty;
        public string Priority => Pr?.Priority ?? ProcurementPriority.Normal;
        public string Description => Pr?.Description ?? string.Empty;
        public int ItemsCount => Pr?.ItemsCount ?? 0;
        public string ItemsSummary => Pr?.ItemsSummary ?? string.Empty;

        [ObservableProperty]
        public partial bool IsKeepCombined { get; set; } = true;
    }
}
