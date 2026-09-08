using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Procure.Data;
using Procure.Data.Repositories;

namespace Procure.Spike.Views;

public sealed partial class PrBoardPage : Page
{
    private readonly IPurchaseRequisitionRepository _repo;
    private DispatcherTimer? _searchDebounce;

    // frame-time meter
    private long _lastTicks;
    private double _emaMs;
    private double _worstMs;
    private DateTime _worstWindowStart = DateTime.UtcNow;

    public PrBoardPage()
    {
        InitializeComponent();
        _repo = new PurchaseRequisitionRepository(new SqliteDatabase());
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        CompositionTarget.Rendering += OnRendering;
        await LoadAsync(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        base.OnNavigatedFrom(e);
    }

    private async System.Threading.Tasks.Task LoadAsync(string? search)
    {
        var sw = Stopwatch.StartNew();
        var db = new SqliteDatabase();
        await db.InitializeAsync();

        var col = new IncrementalPrCollection(_repo, search, DispatcherQueue);
        Board.ItemsSource = col;

        // prime the first page so the list isn't empty on arrival
        await col.LoadMoreItemsAsync(1);
        sw.Stop();

        LoadText.Text = $"load: {sw.ElapsedMilliseconds} ms (first page)";
        CountText.Text = $"{col.Count} loaded · streaming as you scroll";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce?.Stop();
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce!.Stop();
            await LoadAsync(SearchBox.Text);
        };
        _searchDebounce.Start();
    }

    private void Board_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // phased rendering hook — currently a no-op; the template is light enough.
        // Kept as the seam where heavy cells would move to args.RegisterUpdateCallback.
    }

    private void OnRendering(object? sender, object e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastTicks != 0)
        {
            var ms = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;
            _emaMs = _emaMs == 0 ? ms : _emaMs * 0.9 + ms * 0.1;
            if (ms > _worstMs) _worstMs = ms;

            if ((DateTime.UtcNow - _worstWindowStart).TotalSeconds >= 1)
            {
                FpsText.Text = $"frame: {_emaMs:F1} ms  ({1000.0 / Math.Max(_emaMs, 0.01):F0} fps)";
                WorstText.Text = $"worst/1s: {_worstMs:F1} ms";
                _worstMs = 0;
                _worstWindowStart = DateTime.UtcNow;
            }
        }
        _lastTicks = now;
    }

    private async void ScrollStressButton_Click(object sender, RoutedEventArgs e)
    {
        ScrollStressButton.IsEnabled = false;
        var sv = FindScrollViewer(Board);
        if (sv is not null)
        {
            var worst = 0.0;
            _worstMs = 0;
            for (var pass = 0; pass < 2; pass++)
            {
                for (double y = 0; y <= sv.ScrollableHeight; y += sv.ViewportHeight * 0.9)
                {
                    sv.ChangeView(null, y, null, disableAnimation: false);
                    await System.Threading.Tasks.Task.Delay(120);
                    if (_worstMs > worst) worst = _worstMs;
                }
                sv.ChangeView(null, 0, null, true);
                await System.Threading.Tasks.Task.Delay(200);
            }
            WorstText.Text = $"stress worst frame: {worst:F1} ms";
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
