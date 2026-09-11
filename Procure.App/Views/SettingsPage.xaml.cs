using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPageModel Vm { get; }
    private bool _syncing;
    private bool _loaded;

    private record Section(string Key, string Label);

    private static readonly Section[] Sections =
    {
        new("Appearance", "Appearance"),
        new("Navigation", "Navigation & Sidebar"),
        new("Procurement", "Procurement Defaults"),
        new("Columns", "Custom Columns"),
        new("Updates", "Updates"),
        new("Storage", "Storage & Data"),
        new("Shortcuts", "Keyboard Shortcuts"),
    };

    public SettingsPage()
    {
        InitializeComponent();
        Vm = App.Services.GetRequiredService<SettingsPageModel>();
        DataContext = Vm;

        Rail.ItemsSource = Sections;
        Vm.PropertyChanged += OnVmPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncFromVm();
        if (_loaded) return;
        _loaded = true;
        try
        {
            await App.Services.GetRequiredService<Procure.Data.SqliteDatabase>().InitializeAsync();
        }
        catch (Exception ex)
        {
            Procure.Utilities.CrashLog.Write("SettingsPage load failed", ex);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Vm.SelectedSection) or nameof(Vm.SelectedThemeMode) or nameof(Vm.SelectedAccentTheme))
            SyncFromVm();
    }

    private void SyncFromVm()
    {
        _syncing = true;
        Rail.SelectedItem = Sections.FirstOrDefault(s => s.Key == Vm.SelectedSection) ?? Sections[0];
        PaintModeChips();
        foreach (var a in Vm.AvailableAccentThemes) a.IsSelected = a.Id == Vm.SelectedAccentTheme;
        _syncing = false;
    }

    /// <summary>Repaints the three Color Mode pills. Runs on every theme *and* accent change, so
    /// the lit pill always carries the live accent - a plain binding would go stale on an accent
    /// switch, and a Checked visual state goes stale on a light/dark switch.</summary>
    private void PaintModeChips()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["PrimaryTextBrush"];
        var c = accent.Color;
        // Contrast picked from the accent itself, not from a theme brush: the theme dictionaries
        // resolve against the app's theme, which is not yet the one this page is switching to.
        var luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        var onAccent = new SolidColorBrush(luma > 0.6 ? Color.FromArgb(255, 20, 20, 20) : Microsoft.UI.Colors.White);

        void Paint(Border fill, TextBlock text, bool on)
        {
            if (on) { fill.Background = accent; text.Foreground = onAccent; }
            else { fill.ClearValue(Border.BackgroundProperty); text.ClearValue(TextBlock.ForegroundProperty); }
        }

        Paint(DarkFill, DarkText, Vm.SelectedThemeMode is "Dark" or "");
        Paint(LightFill, LightText, Vm.SelectedThemeMode == "Light");
        Paint(SystemFill, SystemText, Vm.SelectedThemeMode == "System");
    }

    private void Rail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Rail.SelectedItem is not Section s) return;
        Vm.SelectSectionCommand.Execute(s.Key);
    }

    private void ThemeMode_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not FrameworkElement { Tag: string mode }) return;
        Vm.SelectThemeModeCommand.Execute(mode);   // SyncFromVm re-checks the right one
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not FrameworkElement { Tag: string id }) return;
        Vm.SelectAccentThemeCommand.Execute(id);   // SyncFromVm flips IsSelected on the list
    }

    private void StageUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string s }) Vm.MoveDefaultStageUpCommand.Execute(s);
    }

    private void StageDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string s }) Vm.MoveDefaultStageDownCommand.Execute(s);
    }

    private void StageRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string s }) Vm.RemoveDefaultStageCommand.Execute(s);
    }

    private void DeleteColumn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CustomColumnDefinition def)
            Vm.ColumnsModel.DeleteColumnCommand.Execute(def);
    }
}
