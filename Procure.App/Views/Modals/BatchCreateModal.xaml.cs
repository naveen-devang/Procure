using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

public sealed partial class BatchCreateModal : UserControl
{
    public BatchCreateModal()
    {
        InitializeComponent();
        Loaded += (_, _) => Procure.App.Platform.FocusOnOpen.Apply(FirstField);   // x:Load builds this modal on every open
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PrItem item && DataContext is PrListPageModel vm)
            vm.RemoveBatchItemFromPrCommand.Execute(item);
    }

    // Leaving an item name fills its estimated price from the last PO for that item (never over a
    // typed price). These PRs are new, so there are no POs of their own to leave out.
    private void ItemName_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PrItem item && DataContext is PrListPageModel vm)
            _ = vm.FillPrItemPricesAsync(new[] { item }, null);
    }
}
