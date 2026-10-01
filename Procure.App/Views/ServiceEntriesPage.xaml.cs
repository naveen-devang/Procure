using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views;

public sealed partial class ServiceEntriesPage : Page
{
    public ServiceEntryPageModel Vm { get; }

    public ServiceEntriesPage()
    {
        Vm = App.Services.GetRequiredService<ServiceEntryPageModel>();
        ServiceEntryColumns.Current.Load(App.Services.GetRequiredService<Procure.Services.ISettingsService>());
        InitializeComponent();
        // A wider column can push the table past the page; it then scrolls sideways.
        ServiceEntryColumns.Current.PropertyChanged += (_, _) => SizeTable();
        Loaded += async (_, _) =>
        {
            Vm.IsVisible = true;
            try { await Vm.EnsureLoadedAsync(); }
            catch (Exception ex) { Procure.Utilities.CrashLog.Write("ServiceEntriesPage load failed", ex); }
        };
        Unloaded += (_, _) => Vm.IsVisible = false;

        // The list's own highlight follows the panel; not bound, because the ListView writes
        // SelectedItem itself when its items are replaced and would knock a binding off.
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Vm.Selected) or nameof(Vm.Entries))
                EntryList.SelectedItem = Vm.Selected is { } s && Vm.Entries.Contains(s) ? s : null;
            if (e.PropertyName == nameof(Vm.Selected))
            {
                var open = Vm.Selected is not null;
                if (open && !_sheetOpen) PlaySheetEntrance();
                _sheetOpen = open;
            }
            if (e.PropertyName == nameof(Vm.IsEditorOpen) && Vm.IsEditorOpen)
                DispatcherQueue.TryEnqueue(() => PoNoBox.Focus(FocusState.Programmatic));
        };

        VendorBox.TextChanged += async (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var text = sender.Text;
            if (string.IsNullOrWhiteSpace(text)) { sender.ItemsSource = null; return; }
            var found = await Vm.FindVendorsAsync(text);
            if (sender.Text == text) sender.ItemsSource = found;
        };
        VendorBox.SuggestionChosen += (sender, args) => sender.Text = args.SelectedItem as string ?? sender.Text;
    }

    private bool _sheetOpen;

    // The PR Board's slide-in: 140 px from the right with a quick fade (PrBoardPage.PlayDetailEntrance).
    private void PlaySheetEntrance()
    {
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var slide = new DoubleAnimation { From = 140, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(360)), EasingFunction = ease };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
        Storyboard.SetTarget(slide, DetailSheetShift);
        Storyboard.SetTargetProperty(slide, "X");
        Storyboard.SetTarget(fade, DetailSheet);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    // Clicking the dimmed table closes the sheet; taps inside the sheet stop at the sheet.
    private void DetailBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Vm.SelectCommand.Execute(null);
        e.Handled = true;
    }

    private void Sheet_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    // Inside a sideways ScrollViewer the table is offered infinite width, which would let the vendor
    // column grow to its longest name. Pin it to the visible width, or the narrowest it reads at.
    private void TableScroll_SizeChanged(object sender, SizeChangedEventArgs e) => SizeTable();

    private void SizeTable() =>
        TableGrid.Width = Math.Max(ServiceEntryColumns.Current.TableMinWidth, TableScroll.ActualWidth);

    private void ResetColumns_Click(object sender, RoutedEventArgs e) => ServiceEntryColumns.Current.ResetAll();

    private void StageBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag) Vm.StageFilter = tag;
    }

    private void EntryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ServiceEntry entry) Vm.SelectCommand.Execute(entry);
    }

    /// <summary>Grows the list near its end, the same trigger the board and Raw &amp; Packing use.</summary>
    private void EntryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemIndex >= Vm.Entries.Count - 8 && Vm.LoadMoreCommand.CanExecute(null))
            Vm.LoadMoreCommand.Execute(null);
    }

    /// <summary>Called from MainWindow's keyboard hook.</summary>
    internal bool HandleShortcut(Windows.System.VirtualKey key, Procure.Services.IKeyboardShortcutService s)
    {
        if (key == Windows.System.VirtualKey.Escape)
        {
            if (Vm.IsEditorOpen) Vm.IsEditorOpen = false;
            else if (Vm.Selected is not null) Vm.SelectCommand.Execute(null);
            else Vm.SearchText = string.Empty;
            return true;
        }
        if (Vm.IsEditorOpen) return false;
        if (Procure.App.Platform.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.FocusSearch), key))
        {
            SearchBox.Focus(FocusState.Keyboard);
            SearchBox.SelectAll();
            return true;
        }
        if (Procure.App.Platform.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.RefreshBoard), key))
        {
            Vm.LoadCommand.Execute(null);
            return true;
        }
        return false;
    }
}
