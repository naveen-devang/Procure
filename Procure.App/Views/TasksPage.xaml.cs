using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views;

public sealed partial class TasksPage : Page
{
    public TodoPageModel Vm { get; }
    private readonly CollectionViewSource _grouped = new() { IsSourceGrouped = true };
    private bool _loaded;

    public TasksPage()
    {
        InitializeComponent();
        Vm = App.Services.GetRequiredService<TodoPageModel>();
        DataContext = Vm;

        _grouped.Source = Vm.Groups;
        TaskList.ItemsSource = _grouped.View;
        Vm.PropertyChanged += OnVmPropertyChanged;
        Vm.WeekRebuilt += () => DispatcherQueue.TryEnqueue(RebuildWeek);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.IsVisible = true;
        if (Content is FrameworkElement root)
        {
            root.KeyDown -= OnKeyDown;
            root.KeyDown += OnKeyDown;
        }
        if (_loaded) { await Vm.RefreshAsync(); return; }
        _loaded = true;
        try
        {
            await App.Services.GetRequiredService<Procure.Data.SqliteDatabase>().InitializeAsync();
            await Vm.LoadAsync();
        }
        catch (Exception ex)
        {
            Procure.Utilities.CrashLog.Write("TasksPage load failed", ex);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Vm.IsVisible = false;
        if (Content is FrameworkElement root) root.KeyDown -= OnKeyDown;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.Groups))
        {
            _grouped.Source = Vm.Groups;
            TaskList.ItemsSource = _grouped.View;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (!string.IsNullOrEmpty(Vm.FilterText)) Vm.FilterText = string.Empty;
            else Vm.SelectCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete && Vm.SelectedTask != null
                 && FocusManager.GetFocusedElement(XamlRoot) is not (TextBox or RichEditBox))
        {
            Vm.DeleteCommand.Execute(Vm.SelectedTask);
            e.Handled = true;
        }
    }

    private void RebuildWeek()
    {
        var tpl = (DataTemplate)Resources["WeekColumnTemplate"];
        WeekGrid.Children.Clear();
        foreach (var col in Vm.WeekColumns)
        {
            var el = (FrameworkElement)tpl.LoadContent();
            el.DataContext = col;
            Grid.SetColumn(el, col.ColIndex);
            WeekGrid.Children.Add(el);
        }
    }

    private static TodoTask? Task(object sender) => (sender as FrameworkElement)?.DataContext as TodoTask;

    private void Stop(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Task(sender) is { } t) Vm.SelectCommand.Execute(t);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (Task(sender) is { } t) Vm.ToggleDoneCommand.Execute(t);
    }

    private void Link_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Task(sender) is { } t) Vm.OpenLinkCommand.Execute(t);
        e.Handled = true;
    }

    private void Seg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string view }) Vm.SetViewCommand.Execute(view);
    }

    private void Composer_GotFocus(object sender, RoutedEventArgs e) => Vm.ComposerOpen = true;

    private void NewTask_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) Vm.QuickAddCommand.Execute(null);
    }

    private void NewPrio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string p }) Vm.SetNewPriorityCommand.Execute(p);
    }

    private void NewDue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string d }) Vm.SetNewDueCommand.Execute(d);
    }

    private async void WeekAdd_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is FrameworkElement { Tag: WeekDayColumn col })
            await Vm.AddWeekColumnTaskAsync(col);
    }

    private void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskLink link) Vm.RemoveLinkCommand.Execute(link);
    }

    private void LinkResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TaskLinkTarget target) Vm.PickLinkTargetCommand.Execute(target);
    }

    private void Subtask_Toggle(object sender, RoutedEventArgs e)
    {
        if (Task(sender) is { } t) Vm.ToggleSubtaskCommand.Execute(t);
    }

    private void Subtask_Delete(object sender, RoutedEventArgs e)
    {
        if (Task(sender) is { } t) Vm.DeleteSubtaskCommand.Execute(t);
    }

    private void Subtask_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) Vm.AddSubtaskCommand.Execute(null);
    }

    private void MarkDone_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedTask is { } t) Vm.ToggleDoneCommand.Execute(t);
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedTask is { } t) Vm.DeleteCommand.Execute(t);
    }
}
