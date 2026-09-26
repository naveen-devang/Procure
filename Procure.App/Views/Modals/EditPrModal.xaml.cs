using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

public sealed partial class EditPrModal : UserControl
{
    public EditPrModal()
    {
        InitializeComponent();
        Loaded += (_, _) => Procure.App.Platform.FocusOnOpen.Apply(FirstField);   // x:Load builds this modal on every open
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PrItem item && DataContext is PrListPageModel vm)
            vm.RemoveEditingPrItemCommand.Execute(item);
    }

    // Leaving an item name fills its estimated price from the last PO for that item (never over a
    // typed price). Its own POs don't count: they are this PR's, not a past purchase.
    private void ItemName_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PrItem item && DataContext is PrListPageModel vm)
            _ = vm.FillPrItemPricesAsync(new[] { item }, vm.CurrentEditingPr?.Id);
    }
}
