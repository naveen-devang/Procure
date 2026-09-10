using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
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
        ThemeDark.IsChecked = Vm.SelectedThemeMode is "Dark" or "";
        ThemeLight.IsChecked = Vm.SelectedThemeMode == "Light";
        ThemeSystem.IsChecked = Vm.SelectedThemeMode == "System";
        AccentGrid.SelectedItem = Vm.AvailableAccentThemes.FirstOrDefault(a => a.Id == Vm.SelectedAccentTheme);
        _syncing = false;
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

    private void Accent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || AccentGrid.SelectedItem is not PastelThemeOption opt) return;
        Vm.SelectAccentThemeCommand.Execute(opt.Id);
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
