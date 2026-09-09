using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Procure.PageModels;

namespace Procure.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPageModel Vm { get; }
    private bool _loaded;

    public DashboardPage()
    {
        InitializeComponent();
        Vm = App.Services.GetRequiredService<DashboardPageModel>();
        DataContext = Vm;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Vm.IsVisible = false;
        Vm.Metrics.NeedsAttentionPrs.CollectionChanged += (_, _) => UpdateEmpty();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.IsVisible = true;
        UpdateEmpty();
        if (_loaded) { _ = Vm.LoadDataCommand.ExecuteAsync(null); return; }
        _loaded = true;
        try
        {
            await App.Services.GetRequiredService<Procure.Data.SqliteDatabase>().InitializeAsync();
            await Vm.LoadDataAsync();
        }
        catch (Exception ex)
        {
            Procure.Utilities.CrashLog.Write("DashboardPage load failed", ex);
        }
        UpdateEmpty();
    }

    private void UpdateEmpty() =>
        EmptyMsg.Visibility = Vm.Metrics.NeedsAttentionPrs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
