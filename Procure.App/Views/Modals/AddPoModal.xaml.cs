using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

public sealed partial class AddPoModal : UserControl
{
    public AddPoModal() => InitializeComponent();

    private PrListPageModel? Vm => DataContext as PrListPageModel;

    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoRfqItemSelection line)
            Vm?.RemovePoItemLineCommand.Execute(line);
    }

    /// <summary>Whole order vs per line. The card is the button's DataContext and the mode its Tag,
    /// which a RelayCommand cannot carry together.</summary>
    private async void TransportMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode, DataContext: PoRfqSelection card }) return;
        if (Vm is { } vm) await vm.SetTransportModeAsync(card, mode);
    }

    private void FillLines_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoRfqSelection card)
            Vm?.FillLinesFromOrderTransportCommand.Execute(card);
    }

    private void LineTransport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoRfqItemSelection line)
            Vm?.ToggleLineTransportCommand.Execute(line);
    }

    private void AddTransport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoRfqItemSelection line)
            Vm?.AddLineTransportCommand.Execute(line);
    }

    private void RemoveTransport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoItemTransport allocation)
            Vm?.RemoveLineTransportCommand.Execute(allocation);
    }

    /// <summary>Typing a contract number that has been used before fills the transporter in.</summary>
    private void OrderTransportEdited(object sender, TextChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoRfqSelection card)
            Vm?.NotifyOrderTransportEdited(card);
    }

    /// <summary>The boxes in an allocation row are bound to the allocation, so the line's running
    /// total hears nothing when one changes. The page model finds the owning line.</summary>
    private void TransportEdited(object sender, TextChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoItemTransport allocation)
            Vm?.NotifyTransportEdited(allocation);
    }
}
