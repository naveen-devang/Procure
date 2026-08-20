using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.Models;
using Procure.PageModels;

namespace Procure.Pages
{
    public partial class PrListPage : ContentPage, IThemeTransitionable
    {
        private readonly PrListPageModel _viewModel;
        private bool _hasLoadedOnce;

        public PrListPage(PrListPageModel viewModel)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;

            // Watch for IsBusy changes to start/stop the shimmer animation
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (!_hasLoadedOnce)
            {
                _hasLoadedOnce = true;
                await _viewModel.LoadPrsAsync();
                WarmDetailTemplate();
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PrListPageModel.IsBusy)) return;

            if (_viewModel.IsBusy)
                StartShimmer();
            else
                StopShimmer();
        }

        private const string ShimmerAnimationKey = "SkeletonShimmerAnimation";

        private void StartShimmer()
        {
            StopShimmer();
            if (SkeletonContainer == null) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var parentAnimation = new Animation();
                var fadeOut = new Animation(v => SkeletonContainer.Opacity = v, 1.0, 0.40, Easing.SinInOut);
                var fadeIn = new Animation(v => SkeletonContainer.Opacity = v, 0.40, 1.0, Easing.SinInOut);

                parentAnimation.Add(0.0, 0.5, fadeOut);
                parentAnimation.Add(0.5, 1.0, fadeIn);

                // ~30fps. MAUI's animation timer is not display-synced, so a faster rate buys no
                // smoothness on an opacity pulse - it only steals main-thread time from the card build.
                parentAnimation.Commit(this, ShimmerAnimationKey, rate: 33, length: 1400, repeat: () => _viewModel.IsBusy);
            });
        }

        private void StopShimmer()
        {
            this.AbortAnimation(ShimmerAnimationKey);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (SkeletonContainer != null)
                {
                    SkeletonContainer.Opacity = 1.0;
                }
            });
        }

        private async void OnApprovalDateSelected(object? sender, DateChangedEventArgs e)
        {
            if (sender is DatePicker picker && picker.BindingContext is Approval approval)
            {
                await _viewModel.HandleApprovalDateChangedAsync(approval);
            }
        }

        private bool _warmedDetailTemplate;

        /// <summary>
        /// Builds one throwaway copy of the card's detail panel after the board is on screen.
        /// Expanding a data-heavy PR was measured at 679 ms on the first click but 232 ms once the
        /// types were warm, so most of that first click is one-off JIT and handler initialisation.
        /// Paying it here moves it off the click path; the instance is discarded immediately.
        /// ponytail: costs one build's worth of allocation at idle. If the detail panel ever grows
        /// enough that this is visible, chunk it or drop it - correctness does not depend on it.
        /// </summary>
        private void WarmDetailTemplate()
        {
            if (_warmedDetailTemplate) return;
            _warmedDetailTemplate = true;

            Dispatcher.Dispatch(() =>
            {
                try
                {
                    var expander = this.GetVisualTreeDescendants()
                                       .OfType<Procure.Utilities.LazyExpander>()
                                       .FirstOrDefault(e => e.ContentTemplate is not null);
                    if (expander?.ContentTemplate?.CreateContent() is View warm)
                    {
                        // Give it a real PR so the bindings and value converters actually run -
                        // without a BindingContext they stay cold and the first real expand still pays.
                        warm.BindingContext = _viewModel.FilteredPrs.FirstOrDefault();
                    }
                }
                catch
                {
                    // Warm-up only - never let it affect the page.
                }
            });
        }

        private void OnPrSelectionCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.BindingContext is PurchaseRequisition pr)
            {
                pr.IsSelected = e.Value;
                _viewModel.UpdateSelectionState();
            }
        }

        private async void OnPrStatusButtonClicked(object? sender, EventArgs e)
        {
            if (sender is Button button && button.BindingContext is PurchaseRequisition pr)
            {
#if WINDOWS
                if (button.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement frameworkElement)
                {
                    var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
                    foreach (var status in ProcurementStatus.AllStatuses)
                    {
                        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = status };
                        if (status == pr.Status)
                        {
                            item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        }
                        item.Click += async (s, args) =>
                        {
                            await _viewModel.UpdatePrStatusDirectAsync(pr, status);
                        };
                        flyout.Items.Add(item);
                    }
                    flyout.ShowAt(frameworkElement);
                    return;
                }
#endif
                await _viewModel.ChangePrStatusAsync(pr);
            }
        }

        private async void OnPoStatusButtonClicked(object? sender, EventArgs e)
        {
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
                            await _viewModel.UpdatePoStatusDirectAsync(po, status);
                        };
                        flyout.Items.Add(item);
                    }
                    flyout.ShowAt(frameworkElement);
                    return;
                }
#endif
                await _viewModel.UpdatePoStatusAsync(po);
            }
        }

        private async void OnRfqStatusButtonClicked(object? sender, EventArgs e)
        {
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
                            await _viewModel.UpdateRfqStatusDirectAsync(rfq, status);
                        };
                        flyout.Items.Add(item);
                    }
                    flyout.ShowAt(frameworkElement);
                    return;
                }
#endif
                await _viewModel.MarkQuoteReceivedCommand.ExecuteAsync(rfq);
            }
        }

        public async Task AnimateThemeTransitionAsync(Action applyTheme, bool isGoingToDark)
        {
            try
            {
                ThemeCurtain.Color = isGoingToDark ? Color.FromArgb("#202020") : Color.FromArgb("#F3F3F3");
                ThemeCurtain.Opacity = 0;
                ThemeCurtain.IsVisible = true;

                // Smooth GPU-accelerated fade-in of curtain (160ms)
                await ThemeCurtain.FadeToAsync(1.0, 160, Easing.SinInOut);

                // Apply theme swap and refresh items behind the solid curtain
                applyTheme();
                await Task.Delay(40);

                // Smooth GPU-accelerated reveal of newly styled UI (200ms)
                await ThemeCurtain.FadeToAsync(0.0, 200, Easing.SinInOut);
            }
            catch
            {
                applyTheme();
            }
            finally
            {
                ThemeCurtain.IsVisible = false;
                ThemeCurtain.Opacity = 0;
            }
        }
    }
}
