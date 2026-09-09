using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

public sealed partial class AddPoModal : UserControl
{
    public AddPoModal() => InitializeComponent();

    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoRfqItemSelection line && DataContext is PrListPageModel vm)
            vm.RemovePoItemLineCommand.Execute(line);
    }
}
