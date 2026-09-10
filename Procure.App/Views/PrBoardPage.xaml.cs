using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Microsoft.Extensions.DependencyInjection;
using Procure.App.Converters;
using Procure.App.Platform;
using Procure.Models;
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
        Vm = App.Services.GetRequiredService<PrListPageModel>();
        DataContext = Vm;
        // Vm is resolved from DI *after* InitializeComponent, so the compiled x:Bind expressions
        // (the modal x:Load flags) evaluated against a null Vm. Re-run them now that Vm is set,
        // which also subscribes them to Vm.PropertyChanged.
        Bindings.Update();
        Loaded += OnLoaded;
        // Leaving the board releases its loaded PR window (only above the VM's 500-row
        // threshold - a light session keeps its place and this is a no-op). The next
        // Loaded -> BoardAppearing reloads the first page. Mirrors what MAUI did in
        // OnDisappearing; WinUI has no page-nav lifecycle so it's wired here.
        Unloaded += (_, _) =>
        {
            if (Vm.BoardDisappearing())
            {
                // Just dropped a few hundred reference-rich PRs (only above the 500-row window -
                // a rare "scrolled deep then left" moment, never on a small database). Compact the
                // gen2 heap so the pages actually return to the OS; off the UI thread so the tab
                // switch isn't waiting on it - the MS-sanctioned "free memory after a heavy
                // sequence" case, and ~50 MB comes back here in practice.
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                });
            }
        };

        // Esc closes the topmost modal. handledEventsToo so a TextBox inside a modal
        // that marks the key handled doesn't swallow it.
        AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), handledEventsToo: true);
        // Click on a modal's dimmed backdrop (the modal UserControl's root Grid, not its
        // content card) closes it too - one handler for all 11 modals.
        AddHandler(TappedEvent, new TappedEventHandler(OnPageTapped), handledEventsToo: true);
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && Vm.CloseTopmostModal())
            e.Handled = true;
    }

    private void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Vm.IsAnyModalVisible
            && e.OriginalSource is Grid { Parent: UserControl } backdrop
            && backdrop.Background is Microsoft.UI.Xaml.Media.SolidColorBrush)
        {
            Vm.CloseTopmostModal();
            e.Handled = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = Vm.SearchText;
        // ComboBox items + selection set from code: the XAML {Binding ItemsSource} + SelectedItem
        // combo never established a visible selection in the port ("blank until you re-pick").
        if (StatusFilterBox.ItemsSource is null)
        {
            StatusFilterBox.ItemsSource = Vm.StatusFilterOptions;
            StatusFilterBox.SelectedItem = Vm.SelectedStatusFilter;
        }

        if (!_loaded)
        {
            _loaded = true;
            Vm.FilteredPrs.CollectionChanged += (_, _) => UpdateEmptyState();
            try
            {
                await App.Services.GetRequiredService<Procure.Data.SqliteDatabase>().InitializeAsync();
            }
            catch (Exception ex)
            {
                Procure.Utilities.CrashLog.Write("PrBoardPage DB init failed", ex);
            }
        }

        // Starts the first load, or reloads if BoardDisappearing released the window;
        // a no-op when the board still holds its data.
        Vm.BoardAppearing();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var empty = Vm.FilteredPrs.Count == 0 && !Vm.IsBusy;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Vm.IsGenuinelyEmpty
            ? "No PR entries yet. Create your first purchase requisition to get started."
            : "No purchase requisitions match your criteria.";
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
        if (!args.InRecycleQueue && args.ItemIndex >= Vm.FilteredPrs.Count - 8)
        {
            if (Vm.LoadMoreCommand.CanExecute(null)) Vm.LoadMoreCommand.Execute(null);
        }
    }

    private void StatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusFilterBox.SelectedItem is string s && s != Vm.SelectedStatusFilter)
            Vm.SelectedStatusFilter = s;
    }


    // ----- card interactions -----

    private static PurchaseRequisition? Pr(object sender) => (sender as FrameworkElement)?.DataContext as PurchaseRequisition;

    private void Child_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;  // don't toggle expand

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Pr(sender) is { } pr) Vm.ToggleExpandCommand.Execute(pr);
    }

    private void PrCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (Pr(sender) is { } pr && sender is CheckBox cb) Vm.SetSelected(pr, cb.IsChecked == true);
    }

    private void Priority_Click(object sender, RoutedEventArgs e)
    {
        if (Pr(sender) is { } pr) Vm.TogglePriorityCommand.Execute(pr);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Pr(sender) is { } pr) Vm.OpenEditPrModalCommand.Execute(pr);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Pr(sender) is { } pr) Vm.DeletePrCommand.Execute(pr);
    }

    private void Status_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || Pr(sender) is not { } pr) return;
        var flyout = new MenuFlyout();
        foreach (var status in ProcurementStatus.SelectableStatuses)
        {
            var item = new MenuFlyoutItem { Text = status };
            if (status == pr.Status) item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var captured = status;
            item.Click += async (_, _) => await Vm.UpdatePrStatusDirectAsync(pr, captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(b);
    }
}
