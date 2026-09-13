using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

public sealed partial class AddRfqModal : UserControl
{
    public AddRfqModal() => InitializeComponent();

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RfqItem item && DataContext is PrListPageModel vm)
            vm.RemoveEditingRfqItemCommand.Execute(item);
    }
}
