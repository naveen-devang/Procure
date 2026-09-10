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
        Unloaded += (_, _) =>
        {
            Vm.IsVisible = false;
            Vm.ReleaseLines();   // collapse every group -> drop the loaded CallOffLine rows
        };
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
