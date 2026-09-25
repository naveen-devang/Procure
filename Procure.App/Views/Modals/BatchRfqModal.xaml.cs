using Microsoft.UI.Xaml.Controls;

namespace Procure.App.Views.Modals;

public sealed partial class BatchRfqModal : UserControl
{
    public BatchRfqModal()
    {
        InitializeComponent();
        Procure.App.Platform.VendorSuggest.Wire(VendorBox, (vm, vendor) => vm.ApplyBatchRfqVendor(vendor));
    }
}
