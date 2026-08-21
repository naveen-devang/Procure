using System;
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
