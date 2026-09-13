using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

public sealed partial class ApprovalConfigModal : UserControl
{
    public ApprovalConfigModal() => InitializeComponent();

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Approval a && DataContext is PrListPageModel vm)
            vm.RemoveStageFromConfigCommand.Execute(a);
    }
}
