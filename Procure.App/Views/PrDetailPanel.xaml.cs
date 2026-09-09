using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views;

/// <summary>
/// The expanded PR card. DataContext is the PurchaseRequisition (inherited from the board's
/// item template); page-model actions route through PrListPageModel.Current, exactly as the
/// MAUI panel did via {x:Static PrListPageModel.Current}.
/// </summary>
public sealed partial class PrDetailPanel : UserControl
{
    private static PrListPageModel? Pm => PrListPageModel.Current;

    public PrDetailPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => Procure.Utilities.TodoChangeNotifier.Changed -= OnTodoChanged;
        DataContextChanged += (_, _) => _ = LoadLinkedTasks();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Procure.Utilities.TodoChangeNotifier.Changed -= OnTodoChanged;
        Procure.Utilities.TodoChangeNotifier.Changed += OnTodoChanged;
        _ = LoadLinkedTasks();
    }

    private void OnTodoChanged() => DispatcherQueue.TryEnqueue(() => _ = LoadLinkedTasks(force: true));

    private Task LoadLinkedTasks(bool force = false) =>
        Pm is { } pm && DataContext is PurchaseRequisition pr ? pm.LoadLinkedTasksAsync(pr, force) : Task.CompletedTask;

    private PurchaseRequisition? Pr => DataContext as PurchaseRequisition;
    private static T? Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    // ---- PR-level ----
    private void SplitMasterPr_Click(object s, RoutedEventArgs e) { if (Pr is { } pr) Pm?.SplitMasterPrCommand.Execute(pr); }
    private void EditPr_Click(object s, RoutedEventArgs e) { if (Pr is { } pr) Pm?.OpenEditPrModalCommand.Execute(pr); }

    // ---- RFQ ----
    private void AddRfq_Click(object s, RoutedEventArgs e) { if (Pr is { } pr) Pm?.OpenAddRfqModalCommand.Execute(pr); }
    private void EditRfq_Click(object s, RoutedEventArgs e) { if (Ctx<RequestForQuotation>(s) is { } r) Pm?.OpenEditRfqModalCommand.Execute(r); }
    private void DeleteRfq_Click(object s, RoutedEventArgs e) { if (Ctx<RequestForQuotation>(s) is { } r) Pm?.DeleteRfqCommand.Execute(r); }
    private void SplitRfq_Click(object s, RoutedEventArgs e) { if (Ctx<RequestForQuotation>(s) is { } r) Pm?.SplitSharedRfqCommand.Execute(r); }

    private void RfqStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || Ctx<RequestForQuotation>(sender) is not { } rfq) return;
        var flyout = new MenuFlyout();
        foreach (var st in RfqStatus.AllStatuses)
        {
            var item = new MenuFlyoutItem { Text = st };
            if (st == rfq.Status) item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var captured = st;
            item.Click += async (_, _) => { if (Pm is { } pm) await pm.UpdateRfqStatusDirectAsync(rfq, captured); };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(b);
    }

    // ---- PCR ----
    private void ExportPcr_Click(object s, RoutedEventArgs e) { if (Pr is { } pr) Pm?.OpenExportPcrModalCommand.Execute(pr); }
    private void ApprovalConfig_Click(object s, RoutedEventArgs e) { if (Pr is { } pr) Pm?.OpenApprovalConfigModalCommand.Execute(pr); }
    private void CreatePcr_Click(object s, RoutedEventArgs e) { if (Pr is { } pr) Pm?.CreatePcrForPrCommand.Execute(pr); }
    private void SetSent_Click(object s, RoutedEventArgs e) { if (Ctx<Approval>(s) is { } a) Pm?.SetApprovalSentTodayCommand.Execute(a); }
    private void SetReceived_Click(object s, RoutedEventArgs e) { if (Ctx<Approval>(s) is { } a) Pm?.SetApprovalReceivedTodayCommand.Execute(a); }
    private void ClearSent_Click(object s, RoutedEventArgs e) { if (Ctx<Approval>(s) is { } a) Pm?.ClearSentDateCommand.Execute(a); }
    private void ClearReceived_Click(object s, RoutedEventArgs e) { if (Ctx<Approval>(s) is { } a) Pm?.ClearReceivedDateCommand.Execute(a); }

    private async void ApprovalDate_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (sender.DataContext is Approval a && Pm is { } pm) await pm.HandleApprovalDateChangedAsync(a);
    }

    // ---- PO ----
    private void AddPo_Click(object s, RoutedEventArgs e) { if (Pr is { } pr) Pm?.OpenAddPoModalCommand.Execute(pr); }
    private void EditPo_Click(object s, RoutedEventArgs e) { if (Ctx<PurchaseOrder>(s) is { } p) Pm?.OpenEditPoModalCommand.Execute(p); }
    private void DeletePo_Click(object s, RoutedEventArgs e) { if (Ctx<PurchaseOrder>(s) is { } p) Pm?.DeletePoCommand.Execute(p); }
    private void SplitPo_Click(object s, RoutedEventArgs e) { if (Ctx<PurchaseOrder>(s) is { } p) Pm?.SplitCombinedPoCommand.Execute(p); }

    private void PoStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || Ctx<PurchaseOrder>(sender) is not { } po) return;
        var flyout = new MenuFlyout();
        foreach (var st in PoStatus.AllStatuses)
        {
            var item = new MenuFlyoutItem { Text = st };
            if (st == po.Status) item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var captured = st;
            item.Click += async (_, _) => { if (Pm is { } pm) await pm.UpdatePoStatusDirectAsync(po, captured); };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(b);
    }

    // ---- linked tasks ----
    private async void LinkedTaskToggle_Click(object s, RoutedEventArgs e)
    {
        if (Ctx<TodoTask>(s) is { } t && Pm is { } pm) await pm.ToggleLinkedTaskAsync(t);
    }

    private async void LinkedTaskDelete_Click(object s, RoutedEventArgs e)
    {
        if (Ctx<TodoTask>(s) is { } t && Pr is { } pr && Pm is { } pm) await pm.DeleteLinkedTaskAsync(pr, t);
    }

    private async void AddTask_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox box || Pr is not { } pr || Pm is not { } pm) return;
        await pm.AddLinkedTaskAsync(pr, box.Text);
        box.Text = string.Empty;
    }
}
