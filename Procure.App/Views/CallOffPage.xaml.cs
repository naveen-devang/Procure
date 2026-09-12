using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views;

public sealed partial class CallOffPage : Page
{
    public CallOffPageModel Vm { get; }
    private bool _loaded;

    public CallOffPage()
    {
        InitializeComponent();
        Vm = App.Services.GetRequiredService<CallOffPageModel>();
        DataContext = Vm;
        Loaded += OnLoaded;
        Vm.PropertyChanged += OnVmPropertyChanged;
        Unloaded += (_, _) =>
        {
            Vm.IsVisible = false;
            Vm.ReleaseLines();   // collapse every group -> drop the loaded CallOffLine rows
        };
    }

    /// <summary>Runs the detail pane's entrance on every selection, not just the first: the pane
    /// is shown by a Visibility binding, and WinUI does not animate a visibility change.</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Vm.SelectedLine) || Vm.SelectedLine is null) return;
        DispatcherQueue.TryEnqueue(() => DetailEntrance.Begin());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.IsVisible = true;
        if (_loaded) { await Vm.RefreshAsync(); return; }
        _loaded = true;
        try
        {
            await App.Services.GetRequiredService<Procure.Data.SqliteDatabase>().InitializeAsync();
            await Vm.LoadAsync();
        }
        catch (Exception ex)
        {
            Procure.Utilities.CrashLog.Write("CallOffPage load failed", ex);
        }
    }

    /// <summary>Grows the material list as the user nears the end of it - the same trigger the board
    /// uses, on the same reasoning: a scroll event fires far more often than a container realizes.</summary>
    private void GroupList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemIndex >= Vm.Groups.Count - 6)
        {
            if (Vm.LoadMoreGroupsCommand.CanExecute(null)) Vm.LoadMoreGroupsCommand.Execute(null);
        }
    }

    private void Group_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MaterialGroup g) Vm.ToggleExpandCommand.Execute(g);
    }

    private void Line_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CallOffLine line) Vm.SelectLineCommand.Execute(line);
        e.Handled = true;
    }

    private void ShowMore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MaterialGroup g) Vm.LoadMoreLinesCommand.Execute(g);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string col }) Vm.SortHistoryCommand.Execute(col);
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PoItemCallOff entry) Vm.DeleteCallOffCommand.Execute(entry);
    }
}
