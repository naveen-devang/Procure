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

    // One timer for the life of the page, restarted on each keystroke. Allocating a fresh
    // DispatcherTimer per character meant a system timer object and a closure for every letter
    // typed into the search box.
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
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
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PrListPageModel.IsDetailPanelOpen) && Vm.IsDetailPanelOpen) ShowDetailPanel();
            else if (e.PropertyName == nameof(PrListPageModel.StatusFilterOptions)) RefreshStatusFilterBox();
            else if (e.PropertyName is nameof(PrListPageModel.SelectedTab) or nameof(PrListPageModel.SearchAllTabs)
                     or nameof(PrListPageModel.TotalFilteredCount) or nameof(PrListPageModel.IsSearchPending))
                SyncTabBar();
            if (e.PropertyName is nameof(PrListPageModel.SelectedTab) or nameof(PrListPageModel.SearchAllTabs)
                or nameof(PrListPageModel.CanSearchAllTabs)) UpdateEmptyState();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Vm.SearchText = SearchBox.Text;   // the VM debounces + reloads FilteredPrs itself
        };

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
                Procure.PageModels.PrListPageModel.CompactInBackground();
            }
        };

        // Click on a modal's dimmed backdrop (the modal UserControl's root Grid, not its
        // content card) closes it too - one handler for all 11 modals.
        AddHandler(TappedEvent, new TappedEventHandler(OnPageTapped), handledEventsToo: true);
    }

    /// <summary>Board keys, called from MainWindow's keyboard hook: Esc closes the topmost dialog,
    /// dialog keys act only on the dialog that is open, board keys only when no dialog is.</summary>
    internal bool HandleShortcut(Windows.System.VirtualKey key, Procure.Services.IKeyboardShortcutService s)
    {
        if (key == Windows.System.VirtualKey.Escape) return Vm.CloseTopmostModal();

        bool Is(string id) => ShortcutInput.Matches(s.GetCombo(id), key);
        var typing = ShortcutInput.IsTextInputFocused(XamlRoot);

        if (Vm.IsPcrPreviewVisible)
        {
            if (Is(Procure.Utilities.KeyboardShortcutIds.PcrPrint)) { Vm.PrintPcrPreviewCommand.Execute(null); return true; }
            if (Is(Procure.Utilities.KeyboardShortcutIds.ModalSave)) { Vm.SavePcrPreviewCommand.Execute(null); return true; }
            if (!typing && Vm.IsPcrPagerVisible && Is(Procure.Utilities.KeyboardShortcutIds.PcrPrevPage)) { Vm.PreviousPcrPreviewPageCommand.Execute(null); return true; }
            if (!typing && Vm.IsPcrPagerVisible && Is(Procure.Utilities.KeyboardShortcutIds.PcrNextPage)) { Vm.NextPcrPreviewPageCommand.Execute(null); return true; }
            // Same step and limits as Ctrl+wheel in the preview (PcrPreviewModal.OnWheel).
            if (Is(Procure.Utilities.KeyboardShortcutIds.PcrZoomIn)) { Vm.PcrPreviewZoom = Math.Clamp(Vm.PcrPreviewZoom * 1.1, 0.25, 6.0); return true; }
            if (Is(Procure.Utilities.KeyboardShortcutIds.PcrZoomOut)) { Vm.PcrPreviewZoom = Math.Clamp(Vm.PcrPreviewZoom / 1.1, 0.25, 6.0); return true; }
            return false;
        }

        if (Is(Procure.Utilities.KeyboardShortcutIds.ModalSave))
        {
            if (Vm.IsEditModalVisible) { Vm.SavePrModalCommand.Execute(null); return true; }
            if (Vm.IsAddRfqModalVisible) { Vm.SaveNewRfqCommand.Execute(null); return true; }
            if (Vm.IsAddPoModalVisible) { Vm.SaveNewPoCommand.Execute(null); return true; }
            if (Vm.IsApprovalConfigModalVisible) { Vm.SaveApprovalConfigModalCommand.Execute(null); return true; }
            if (Vm.IsMergePrModalVisible) { Vm.ConfirmMergePrModalCommand.Execute(null); return true; }
            if (Vm.IsSplitPrModalVisible) { Vm.ConfirmSplitPrModalCommand.Execute(null); return true; }
            if (Vm.IsBatchCreateModalVisible) { Vm.SaveBatchPrsModalCommand.Execute(null); return true; }
            if (Vm.IsBatchRfqModalVisible) { Vm.SaveBatchRfqModalCommand.Execute(null); return true; }
            if (Vm.IsBatchPoModalVisible) { Vm.SaveBatchPoModalCommand.Execute(null); return true; }
            return false;
        }

        if (!typing && Is(Procure.Utilities.KeyboardShortcutIds.ModalSelectAll))
        {
            if (Vm.IsBatchRfqModalVisible) { Vm.SelectAllBatchRfqItems(); return true; }
            if (Vm.IsAddPoModalVisible) { Vm.SelectAllPoRfqs(); return true; }
            return false;
        }

        if (Vm.IsAnyModalVisible) return false;

        if (Is(Procure.Utilities.KeyboardShortcutIds.FocusSearch)) { SearchBox.Focus(FocusState.Keyboard); SearchBox.SelectAll(); return true; }
        if (Is(Procure.Utilities.KeyboardShortcutIds.NewPr)) { Vm.OpenBatchCreateModalCommand.Execute(null); return true; }
        if (Is(Procure.Utilities.KeyboardShortcutIds.RefreshBoard)) { Vm.RefreshBoardCommand.Execute(null); return true; }
        if (Is(Procure.Utilities.KeyboardShortcutIds.ExportCsv)) { Vm.ExportCsvCommand.Execute(null); return true; }
        return false;
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
        // Re-set every time Loaded fires (not just the first time) - this page is a DI singleton,
        // so Loaded refires on every nav back to the board, and the ComboBox's displayed content
        // goes blank on that revisit if it isn't nudged again.
        RefreshStatusFilterBox();
        SyncTabBar();

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
        // Empty with no filter at all is a statement about the tab, not the database: a database whose
        // every PR is finished has an empty Active tab and plenty of PRs.
        EmptyText.Text = Vm.IsGenuinelyEmpty
            ? Vm.SelectedTab switch
            {
                Procure.Models.BoardTab.Completed => "Nothing has been completed yet.",
                Procure.Models.BoardTab.OnHold => "Nothing is on hold or cancelled.",
                _ => "No active requisitions. Add one with + Add PR, or look in the Completed tab.",
            }
            : Vm.CanSearchAllTabs
                ? $"Nothing in {Procure.Models.BoardTabs.Title(Vm.SelectedTab)} matches your search."
                : "No purchase requisitions match your criteria.";
    }

    // ----- tabs -----

    private bool _syncingTabs;

    private void TabBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingTabs) return;
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<Procure.Models.BoardTab>(tag, out var tab))
            Vm.SelectTab(tab);
    }

    /// <summary>Puts the tab strip in step with the view model: the open tab selected (none while
    /// searching across all tabs) and its match count beside its name.</summary>
    private void SyncTabBar()
    {
        _syncingTabs = true;
        try
        {
            var items = new[] { (TabActive, Procure.Models.BoardTab.Active), (TabCompleted, Procure.Models.BoardTab.Completed), (TabOnHold, Procure.Models.BoardTab.OnHold) };
            foreach (var (item, tab) in items)
            {
                var open = !Vm.SearchAllTabs && Vm.SelectedTab == tab;
                item.Text = open && !Vm.IsSearchPending
                    ? $"{Procure.Models.BoardTabs.Title(tab)}  {Vm.TotalFilteredCount:N0}"
                    : Procure.Models.BoardTabs.Title(tab);
                if (open && TabBar.SelectedItem != item) TabBar.SelectedItem = item;
            }
            if (Vm.SearchAllTabs && TabBar.SelectedItem is not null) TabBar.SelectedItem = null;
        }
        finally { _syncingTabs = false; }
    }

    /// <summary>The dropdown only offers the open tab's statuses, so it is rebuilt whenever the tab or
    /// the search scope changes.</summary>
    private void RefreshStatusFilterBox()
    {
        StatusFilterBox.ItemsSource = Vm.StatusFilterOptions;
        StatusFilterBox.SelectedItem = Vm.SelectedStatusFilter;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Stop-then-start restarts the interval, so the tick only lands 250ms after the last
        // keystroke - the same trailing debounce, without building a new timer each time.
        _searchDebounce.Stop();
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

    /// <summary>The detail slide-over is built the first time a PR is opened and kept after that; its
    /// Visibility follows IsDetailPanelOpen (see the comment on DetailOverlay in the XAML for why).</summary>
    private void ShowDetailPanel()
    {
        if (DetailOverlay is null) FindName(nameof(DetailOverlay));
        PlayDetailEntrance();
    }

    // The slide-in the EntranceThemeTransition used to give it on each (re)build: 140 px from the right.
    private void PlayDetailEntrance()
    {
        if (DetailSheet is null || DetailSheetShift is null) return;
        var ease = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut, Exponent = 6 };
        var duration = new Duration(TimeSpan.FromMilliseconds(360));
        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 140, To = 0, Duration = duration, EasingFunction = ease };
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, DetailSheetShift);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "X");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, DetailSheet);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    /// <summary>Click the dimmed board behind the detail slide-over to close it. The panel itself
    /// swallows taps via Child_Tapped, so this only fires on the backdrop.</summary>
    private void DetailBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Vm.CloseDetailPanelCommand.Execute(null);
        e.Handled = true;
    }

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
