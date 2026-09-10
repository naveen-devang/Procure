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
        Board.ItemsSource = Vm.FilteredPrs;
        SearchBox.Text = Vm.SearchText;

        if (!_loaded)
        {
            _loaded = true;
            Vm.FilteredPrs.CollectionChanged += (_, _) => UpdateEmptyState();
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
        }
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

    private void OverdueChip_Click(object sender, RoutedEventArgs e) => Vm.ToggleFilterOverdueCommand.Execute(null);
    private void PcrPendingChip_Click(object sender, RoutedEventArgs e) => Vm.ToggleFilterPcrPendingCommand.Execute(null);
    private void UrgentChip_Click(object sender, RoutedEventArgs e) => Vm.ToggleFilterUrgentCommand.Execute(null);

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
