using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

public sealed partial class BatchCreateModal : UserControl
{
    public BatchCreateModal() => InitializeComponent();

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PrItem item && DataContext is PrListPageModel vm)
            vm.RemoveBatchItemFromPrCommand.Execute(item);
    }
}
