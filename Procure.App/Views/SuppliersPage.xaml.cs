using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Procure.App.Converters;
using Procure.Models;
using Procure.PageModels;
using Windows.ApplicationModel.DataTransfer;

namespace Procure.App.Views;

public sealed partial class SuppliersPage : Page
{
    public SuppliersPageModel Vm { get; }

    private readonly List<ToggleButton> _periodChips = new();
    private readonly List<ToggleButton> _tagChips = new();

    public SuppliersPage()
    {
        InitializeComponent();
        Vm = App.Services.GetRequiredService<SuppliersPageModel>();

        // The filter chips under the search box, and the same three period chips on both views.
        foreach (var (label, tag) in new[] { ("All", ""), ("Preferred", SupplierTag.Preferred), ("Avoid", SupplierTag.Avoid) })
            TagChips.Children.Add(Chip(label, tag, _tagChips, TagChip_Click));
        foreach (var host in new[] { SupplierPeriodChips, ItemPeriodChips })
            foreach (var period in Vm.PeriodOptions)
                host.Children.Add(Chip(period, period, _periodChips, PeriodChip_Click));
        PaintAll();

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SuppliersPageModel.Period) or nameof(SuppliersPageModel.IsItemsMode)
                or nameof(SuppliersPageModel.TagFilter)) PaintAll();
        };

        Loaded += OnLoaded;
        Unloaded += (_, _) => Vm.IsVisible = false;
        // Every list is read a page at a time; the next page is fetched as a list nears its end.
        SupplierList.Loaded += (_, _) => WatchScroll(SupplierList, () => _ = Vm.LoadMoreAsync());
        ItemList.Loaded += (_, _) => WatchScroll(ItemList, () => _ = Vm.LoadMoreAsync());
        VendorItemList.Loaded += (_, _) => WatchScroll(VendorItemList, () => _ = Vm.LoadMoreVendorItemsAsync());
    }

    public string BoughtLabel(string period) => period == "All" ? "Bought from them, all time" : $"Bought from them, {period}";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.IsVisible = true;
        try
        {
            await Vm.LoadAsync();
        }
        catch (Exception ex)
        {
            Procure.Utilities.CrashLog.Write("SuppliersPage load failed", ex);
        }
    }

    // ---- chips and the Suppliers / Items switch -------------------------------------------------

    /// <summary>A stock WinUI toggle button; the checked one is the current choice.</summary>
    private static ToggleButton Chip(string label, string tag, List<ToggleButton> group, RoutedEventHandler click)
    {
        var chip = new ToggleButton { Content = label, Tag = tag };
        chip.Click += click;
        group.Add(chip);
        return chip;
    }

    private void PaintAll()
    {
        // Single choice: clicking the chip that is already on must leave it on.
        foreach (var chip in _tagChips) chip.IsChecked = (string)chip.Tag == Vm.TagFilter;
        foreach (var chip in _periodChips) chip.IsChecked = (string)chip.Tag == Vm.Period;
        SupplierLook.PaintSegment(SuppliersTab, !Vm.IsItemsMode);
        SupplierLook.PaintSegment(ItemsTab, Vm.IsItemsMode);
    }

    private void Mode_Click(object sender, RoutedEventArgs e) =>
        Vm.IsItemsMode = (string)((FrameworkElement)sender).Tag == "items";

    private void TagChip_Click(object sender, RoutedEventArgs e)
    {
        Vm.TagFilter = (string)((FrameworkElement)sender).Tag;
        PaintAll();
    }

    private void PeriodChip_Click(object sender, RoutedEventArgs e)
    {
        Vm.Period = (string)((FrameworkElement)sender).Tag;
        PaintAll();
    }

    // ---- rows ---------------------------------------------------------------------------------------

    private void Supplier_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SupplierListItem item)
            Vm.SelectSupplierCommand.Execute(item);
    }

    private void Item_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ItemListItem item)
            Vm.SelectItemCommand.Execute(item);
    }

    private void TreatAsOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ItemListItem item) _ = Vm.TreatAsOneAsync(item);
    }

    private void VendorItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VendorItemRow row) _ = Vm.ToggleVendorItemAsync(row);
    }

    private void OpenItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VendorItemRow row) _ = Vm.OpenItemAsync(row.Key);
    }

    private void SetTag_Click(object sender, RoutedEventArgs e) => _ = Vm.SetTagAsync((string)((FrameworkElement)sender).Tag);

    private void SaveContact_Click(object sender, RoutedEventArgs e)
    {
        ContactFlyout.Hide();
        Vm.SaveContactCommand.Execute(null);
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        Vm.AddCategory(NewCategory.Text);
        NewCategory.Text = string.Empty;
    }

    /// <summary>Enter adds the category, as the Add button does.</summary>
    private void NewCategory_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        AddCategory_Click(sender, e);
    }

    private void RemoveCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string category) Vm.RemoveCategory(category);
    }

    private void Separate_Click(object sender, RoutedEventArgs e)
    {
        // One alias at a time, most recent first - the common case is undoing the one just made.
        if (Vm.SelectedItem?.Aliases.LastOrDefault() is { Key.Length: > 0 } alias) _ = Vm.SeparateAsync(alias.Key);
    }

    private void NoteFlyout_Opening(object? sender, object e)
    {
        var months = Vm.NoteMonths();
        NoteFrom.ItemsSource = months;
        NoteTo.ItemsSource = months;
        NoteFrom.SelectedIndex = months.Count > 0 ? 0 : -1;
        NoteTo.SelectedIndex = months.Count > 0 ? 0 : -1;
        NoteText.Text = string.Empty;
    }

    private void AddNote_Click(object sender, RoutedEventArgs e)
    {
        if (NoteFrom.SelectedIndex < 0 || NoteTo.SelectedIndex < 0 || string.IsNullOrWhiteSpace(NoteText.Text)) return;
        NoteFlyout.Hide();
        _ = Vm.AddNoteAsync(NoteFrom.SelectedIndex, NoteTo.SelectedIndex, NoteText.Text);
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) _ = Vm.DeleteNoteAsync(id);
    }

    private async void CopyTop_Click(object sender, RoutedEventArgs e)
    {
        var text = Vm.TopThreeText();
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        CopyTopButton.Content = "Copied";
        await System.Threading.Tasks.Task.Delay(1800);
        CopyTopButton.Content = "Copy top 3 (names + emails)";
    }

    private readonly HashSet<ListView> _watched = new();

    private void WatchScroll(ListView list, Action nearEnd)
    {
        if (!_watched.Add(list) || FindScrollViewer(list) is not { } sv) return;
        sv.ViewChanged += (_, _) =>
        {
            if (sv.ScrollableHeight > 0 && sv.VerticalOffset >= sv.ScrollableHeight - 200) nearEnd();
        };
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}

/// <summary>
/// The Suppliers page's colours, as the design has them. Every brush is a live BoardTheme brush, so
/// a Light / Dark switch recolours it in place (never Application.Current.Resources, which keeps the
/// start-up theme). Used from the page's x:Bind functions and to paint the chips.
/// </summary>
public static class SupplierLook
{
    private static SolidColorBrush T(string key) => BoardTheme.Themed(key);
    private static readonly SolidColorBrush Clear = BoardTheme.Brush("#00000000");
    /// <summary>The design's soft blue edge (#CCE4F7) on a selected row or an open row.</summary>
    private static SolidColorBrush SoftEdge => BoardTheme.Pick("#2A527A", "#CCE4F7");

    public static Brush RowBg(bool selected) => selected ? T("FluentInfoBg") : Clear;
    public static Brush RowBorder(bool selected) => selected ? SoftEdge : T("AppHairline");
    public static Thickness SolidEdge(bool dashed) => dashed ? new Thickness(0) : new Thickness(1);
    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Brush TagInk(string tag) =>
        tag == SupplierTag.Preferred ? T("FluentSuccess") : tag == SupplierTag.Avoid ? T("FluentCritical") : Clear;

    public static Brush Ink(bool faded) => faded ? T("AppTextTertiary") : T("AppTextPrimary");
    public static Brush QuietInk(bool faded) => faded ? T("AppTextTertiary") : T("AppTextSecondary");
    public static Brush RankInk(bool top, bool faded) => faded ? T("AppTextTertiary") : top ? T("AppTextPrimary") : T("AppTextSecondary");
    /// <summary>Green well below the market then, red well above.</summary>
    public static Brush ToneInk(int tone, bool faded) =>
        faded ? T("AppTextTertiary") : tone < 0 ? T("FluentSuccess") : tone > 0 ? T("FluentCritical") : T("AppTextPrimary");
    public static Windows.UI.Text.FontWeight ToneWeight(int tone, bool faded) => !faded && tone != 0 ? FontWeights.SemiBold : FontWeights.Normal;
    public static Brush BestInk(bool best, bool old) => old ? T("AppTextTertiary") : best ? T("FluentSuccess") : T("AppTextPrimary");
    public static Windows.UI.Text.FontWeight Weight(bool bold) => bold ? FontWeights.SemiBold : FontWeights.Normal;

    // An open item row is a blue-edged card with a tinted header; a closed one is a ruled line.
    public static Thickness OpenEdge(bool open) => open ? new Thickness(1) : new Thickness(0, 0, 0, 1);
    public static CornerRadius OpenCorners(bool open) => new(open ? 8 : 0);
    public static Brush OpenBorder(bool open) => open ? SoftEdge : T("AppHairline");
    public static Brush OpenHeader(bool open) => open ? T("FluentInfoBg") : Clear;
    public static Thickness OpenGap(bool open) => open ? new Thickness(0, 4, 0, 6) : new Thickness(0);

    public static void PaintSegment(Button segment, bool on)
    {
        segment.Background = on ? T("AppSecondaryBackground") : Clear;
        segment.Foreground = on ? T("AppTextPrimary") : T("AppTextSecondary");
        segment.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
