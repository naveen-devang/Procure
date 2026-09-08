using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Microsoft.Extensions.DependencyInjection;
using Procure.PageModels;

namespace Procure.App.Views;

public sealed partial class PrBoardPage : Page
{
    public PrListPageModel Vm { get; }

    private DispatcherTimer? _searchDebounce;
    private bool _loaded;

    public PrBoardPage()
    {
        InitializeComponent();
        Vm = (PrListPageModel)App.Services.GetService(typeof(PrListPageModel))!;
        DataContext = Vm;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Board.ItemsSource = Vm.FilteredPrs;
        SearchBox.Text = Vm.SearchText;
        if (_loaded) return;
        _loaded = true;
        try
        {
            await App.Services.GetRequiredService<Procure.Data.SqliteDatabase>().InitializeAsync();
            await Vm.LoadPrsAsync();
            await Task.Delay(300);
            Procure.Utilities.CrashLog.Write($"PrBoardPage load: FilteredPrs={Vm.FilteredPrs.Count} total={Vm.TotalFilteredCount}");
        }
        catch (Exception ex)
        {
            Procure.Utilities.CrashLog.Write("PrBoardPage load failed", ex);
        }
        CountText.Text = $"{Vm.FilteredPrs.Count} of {Vm.TotalFilteredCount}";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce?.Stop();
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce!.Stop();
            Vm.SearchText = SearchBox.Text;   // the VM debounces + reloads FilteredPrs itself
        };
        _searchDebounce.Start();
    }

    private void Board_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Near the end of the realized set -> ask the VM for the next page (its RemainingItemsThreshold).
        if (!args.InRecycleQueue && args.ItemIndex >= Vm.FilteredPrs.Count - 8)
        {
            if (Vm.LoadMoreCommand.CanExecute(null)) Vm.LoadMoreCommand.Execute(null);
        }
    }

    private async void ScrollStressButton_Click(object sender, RoutedEventArgs e)
    {
        ScrollStressButton.IsEnabled = false;
        var sv = FindScrollViewer(Board);
        if (sv is not null)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                for (double y = 0; y <= sv.ScrollableHeight; y += sv.ViewportHeight * 0.9)
                {
                    sv.ChangeView(null, y, null, disableAnimation: false);
                    await Task.Delay(120);
                }
                sv.ChangeView(null, 0, null, true);
                await Task.Delay(200);
            }
        }
        ScrollStressButton.IsEnabled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var hit = FindScrollViewer(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
            if (hit is not null) return hit;
        }
        return null;
    }
}
