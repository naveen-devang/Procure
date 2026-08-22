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

        public PrListPage(PrListPageModel viewModel)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;

            // Watch for IsBusy changes to start/stop the shimmer animation
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        // Whether the data is already warm, still loading, or untouched is the page model's business -
        // it owns the rows. This page only reports when the board can actually paint.
        protected override void OnAppearing()
        {
            base.OnAppearing();
            _viewModel.BoardAppearing();

#if DEBUG
            if (Procure.Utilities.BoardBench.IsEnabled && !_benchStarted)
            {
                _benchStarted = true;
                Dispatcher.Dispatch(async () => await Procure.Utilities.BoardBench.RunAsync(
                    () => _viewModel.FilteredPrs.Count,
                    _viewModel.RevealMore,
                    () => Procure.Utilities.BoardBench.SettleAsync(Dispatcher)));
            }
#endif
        }

#if DEBUG
        private bool _benchStarted;
#endif

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _viewModel.BoardDisappearing();
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

        // One card's worth of runway, so the next batch is built before the user reaches the end.
        private const double RevealThreshold = 400;
        private bool _revealQueued;

        private void OnBoardScrolled(object? sender, ScrolledEventArgs e)
        {
            // Scrolled fires many times per gesture and the list grows only after the next layout
            // pass, so without this latch one flick near the bottom reveals the whole list at once.
            if (_revealQueued) return;
            if (e.ScrollY + BoardScroll.Height < BoardScroll.ContentSize.Height - RevealThreshold) return;

            _revealQueued = true;
            _viewModel.RevealMore();
            Dispatcher.Dispatch(() => _revealQueued = false);
        }

        private void OnPrSelectionCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.BindingContext is PurchaseRequisition pr)
            {
                _viewModel.SetSelected(pr, e.Value);
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
