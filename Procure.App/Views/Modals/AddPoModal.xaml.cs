using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

public sealed partial class AddPoModal : UserControl
{
    private readonly PoStep2RowList _step2 = new();
    private PrListPageModel? _hooked;

    public AddPoModal()
    {
        InitializeComponent();
        Step2List.ItemsSource = _step2.Rows;
        Loaded += (_, _) => Attach();
        // The modal is x:Load-ed per open and unloaded on close; the page model and its cards are not,
        // so everything this subscribed to is let go here.
        Unloaded += (_, _) => Detach();
    }

    private PrListPageModel? Vm => DataContext as PrListPageModel;

    private void Attach()
    {
        Detach();
        if (Vm is not { } vm) return;
        _hooked = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        if (vm.IsPoModalStep2) _step2.Build(vm.PoRfqSelections);   // Edit PO opens straight on step 2
    }

    private void Detach()
    {
        if (_hooked is not null) _hooked.PropertyChanged -= OnVmPropertyChanged;
        _hooked = null;
        _step2.Detach();
    }

    /// <summary>The rows are cut from the cards each time step 2 is entered - going back to step 1 is the
    /// only way to change which suppliers are selected, so that is the only moment the set can change.</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.PropertyName is nameof(PrListPageModel.IsPoModalStep2) or nameof(PrListPageModel.PoRfqSelections))
        {
            if (vm.IsPoModalStep2) _step2.Build(vm.PoRfqSelections);
            else _step2.Detach();
        }
    }

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

    /// <summary>The boxes in an allocation row are bound to the allocation, so the line's running
    /// total hears nothing when one changes. The page model finds the owning line.</summary>
    private void TransportEdited(object sender, TextChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoItemTransport allocation)
            Vm?.NotifyTransportEdited(allocation);
    }
}
