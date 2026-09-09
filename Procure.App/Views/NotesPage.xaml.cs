using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views;

public sealed partial class NotesPage : Page
{
    public NotePageModel Vm { get; }
    private bool _loaded;

    public NotesPage()
    {
        InitializeComponent();
        Vm = App.Services.GetRequiredService<NotePageModel>();
        DataContext = Vm;

        Vm.EditorLoadRequested += rtf => DispatcherQueue.TryEnqueue(() => Editor.Load(rtf));
        Editor.ContentChanged += (_, e) => Vm.OnBodyEdited(e.rtf, e.plainText);

        Loaded += OnLoaded;
        Unloaded += (_, _) => _ = Vm.FlushPendingAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            await App.Services.GetRequiredService<Procure.Data.SqliteDatabase>().InitializeAsync();
            await Vm.LoadListAsync();
        }
        catch (Exception ex)
        {
            Procure.Utilities.CrashLog.Write("NotesPage load failed", ex);
        }
    }

    private void Note_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoteListItem item)
            Vm.SelectCommand.Execute(item);
    }

    private void Fmt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action }) Editor.Apply(action);
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => Vm.TogglePinCommand.Execute(null);
    private void Duplicate_Click(object sender, RoutedEventArgs e) => Vm.DuplicateCommand.Execute(null);
    private void Delete_Click(object sender, RoutedEventArgs e) => Vm.DeleteCommand.Execute(null);

    private void LinkOpen_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Vm.OpenLinkCommand.Execute(null);
        e.Handled = true;
    }

    private void LinkRemove_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoteLink link) Vm.RemoveLinkCommand.Execute(link);
        e.Handled = true;
    }

    private void LinkResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TaskLinkTarget target) Vm.PickLinkTargetCommand.Execute(target);
    }
}
