using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.Pages.Controls
{
    /// <summary>
    /// The expanded detail panel of a PR card. Its BindingContext is the
    /// <see cref="PurchaseRequisition"/>, inherited from the DataTemplate that builds it, so item
    /// bindings inside the XAML are plain. Page-model commands cannot reach the page's namescope
    /// from here, so the page passes its own BindingContext down through <see cref="PageModel"/>
    /// and the XAML binds through <c>{x:Reference ThisPanel}</c>.
    /// </summary>
    public partial class PrDetailPanel : ContentView
    {
        public static readonly BindableProperty PageModelProperty = BindableProperty.Create(
            nameof(PageModel), typeof(PrListPageModel), typeof(PrDetailPanel));

        public PrListPageModel? PageModel
        {
            get => (PrListPageModel?)GetValue(PageModelProperty);
            set => SetValue(PageModelProperty, value);
        }

        public PrDetailPanel()
        {
            InitializeComponent();
            Loaded += OnPanelLoaded;
            Unloaded += OnPanelUnloaded;
        }

        private void OnPanelLoaded(object? sender, EventArgs e)
        {
            Procure.Utilities.TodoChangeNotifier.Changed -= OnTodoChanged;
            Procure.Utilities.TodoChangeNotifier.Changed += OnTodoChanged;
            _ = LoadLinkedTasksAsync();
        }

        private void OnPanelUnloaded(object? sender, EventArgs e) =>
            Procure.Utilities.TodoChangeNotifier.Changed -= OnTodoChanged;

        // A linked task changed somewhere (this panel, another panel, or the Tasks page) - reload
        // this PR's strip from the database so it stays in sync in real time.
        private void OnTodoChanged() =>
            MainThread.BeginInvokeOnMainThread(() => _ = LoadLinkedTasksAsync(force: true));

        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();
            _ = LoadLinkedTasksAsync();
        }

        private Task LoadLinkedTasksAsync(bool force = false) =>
            PageModel is { } pm && BindingContext is PurchaseRequisition pr
                ? pm.LoadLinkedTasksAsync(pr, force)
                : Task.CompletedTask;

        private async void OnAddTaskEntryCompleted(object? sender, EventArgs e)
        {
            if (PageModel is null || BindingContext is not PurchaseRequisition pr) return;
            if (sender is Entry entry)
            {
                await PageModel.AddLinkedTaskAsync(pr, entry.Text);
                entry.Text = string.Empty;
            }
        }

        private async void OnLinkedTaskToggle(object? sender, EventArgs e)
        {
            if (PageModel is not null && sender is Button b && b.BindingContext is TodoTask t)
                await PageModel.ToggleLinkedTaskAsync(t);
        }

        private async void OnLinkedTaskDelete(object? sender, EventArgs e)
        {
            if (PageModel is { } pm && BindingContext is PurchaseRequisition pr
                && sender is Button b && b.BindingContext is TodoTask t)
                await pm.DeleteLinkedTaskAsync(pr, t);
        }

        private async void OnApprovalDateSelected(object? sender, DateChangedEventArgs e)
        {
            if (PageModel is null) return;

            if (sender is DatePicker picker && picker.BindingContext is Approval approval)
            {
                await PageModel.HandleApprovalDateChangedAsync(approval);
            }
        }

        private async void OnPoStatusButtonClicked(object? sender, EventArgs e)
        {
            if (PageModel is null) return;

            if (sender is Button button && button.BindingContext is PurchaseOrder po)
            {
#if WINDOWS
                if (button.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement frameworkElement)
                {
                    var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
                    foreach (var status in PoStatus.AllStatuses)
                    {
                        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = status };
                        if (status == po.Status)
                        {
                            item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        }
                        item.Click += async (s, args) =>
                        {
                            await PageModel.UpdatePoStatusDirectAsync(po, status);
                        };
                        flyout.Items.Add(item);
                    }
                    flyout.ShowAt(frameworkElement);
                    return;
                }
#endif
                await PageModel.UpdatePoStatusAsync(po);
            }
        }

        private async void OnRfqStatusButtonClicked(object? sender, EventArgs e)
        {
            if (PageModel is null) return;

            if (sender is Button button && button.BindingContext is RequestForQuotation rfq)
            {
#if WINDOWS
                if (button.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement frameworkElement)
                {
                    var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
                    foreach (var status in RfqStatus.AllStatuses)
                    {
                        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = status };
                        if (status == rfq.Status)
                        {
                            item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        }
                        item.Click += async (s, args) =>
                        {
                            await PageModel.UpdateRfqStatusDirectAsync(rfq, status);
                        };
                        flyout.Items.Add(item);
                    }
                    flyout.ShowAt(frameworkElement);
                    return;
                }
#endif
                await PageModel.MarkQuoteReceivedCommand.ExecuteAsync(rfq);
            }
        }
    }
}
