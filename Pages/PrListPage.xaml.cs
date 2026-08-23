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
            Procure.Utilities.BoardTrace.Mark("page-inflate-start");
            InitializeComponent();
            Procure.Utilities.BoardTrace.Mark("page-inflate-done");
            BindingContext = _viewModel = viewModel;

            // Watch for IsBusy changes to start/stop the shimmer animation
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        // Whether the data is already warm, still loading, or untouched is the page model's business -
        // it owns the rows. This page only reports when the board can actually paint.
        protected override void OnAppearing()
        {
            base.OnAppearing();
            Procure.Utilities.BoardTrace.Mark("page-appearing");
            _viewModel.BoardAppearing();

#if WINDOWS
            // Post-layout, so the ListView's template (and its ScrollViewer) exists by the time we
            // look for it. One delayed retry covers a slow first layout.
            Dispatcher.Dispatch(() =>
            {
                if (!HookBoardScrollViewer())
                    Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), () => HookBoardScrollViewer());
            });
#endif

#if DEBUG
            if (Procure.Utilities.BoardBench.IsEnabled && !_benchStarted)
            {
                _benchStarted = true;
                Dispatcher.Dispatch(async () => await Procure.Utilities.BoardBench.RunAsync(
                    () => _viewModel.FilteredPrs.Count,
                    () => _viewModel.LoadMoreCommand.Execute(null),
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

#if WINDOWS
        // One page-level Esc hook for all ten modal overlays. Subscribe/unsubscribe tied to the
        // handler lifetime, same as AppShell's event pairs.
        protected override void OnHandlerChanged()
        {
            base.OnHandlerChanged();
            if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement root)
            {
                root.PreviewKeyDown -= OnPagePreviewKeyDown;
                root.PreviewKeyDown += OnPagePreviewKeyDown;
            }
        }

        protected override void OnHandlerChanging(HandlerChangingEventArgs args)
        {
            base.OnHandlerChanging(args);
            if (args.NewHandler is null)
            {
                if (args.OldHandler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement root)
                {
                    root.PreviewKeyDown -= OnPagePreviewKeyDown;
                }
                if (_boardScrollViewer != null)
                {
                    _boardScrollViewer.ViewChanged -= OnBoardViewChanged;
                    _boardScrollViewer = null;
                }
            }
        }

        private void OnPagePreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            if (_viewModel.CloseTopmostModal()) e.Handled = true;
        }
#endif

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PrListPageModel.IsToastVisible) && _viewModel.IsToastVisible)
            {
                ToastPill.Opacity = 0;
                _ = ToastPill.FadeToAsync(1.0, 150, Easing.CubicOut);
                return;
            }

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

        // MAUI-level scroll trigger; kept, but on a prewarmed page it can stay dead until the page
        // is left and revisited - the native ViewChanged hook below is the authoritative signal.
        private void OnBoardScrolled(object? sender, ItemsViewScrolledEventArgs e) =>
            _viewModel.OnBoardScrolled(e.LastVisibleItemIndex);

#if WINDOWS
        private Microsoft.UI.Xaml.Controls.ScrollViewer? _boardScrollViewer;

        // Hooks the board ListView's ScrollViewer directly: on a page inflated by the prewarm, both
        // MAUI scroll events (Scrolled, RemainingItemsThresholdReached) never fire on the first
        // visit - traced live - because their wiring predates the native ScrollViewer. ViewChanged
        // also fires when appended pages grow the extent, which keeps a user parked at the bottom fed.
        private bool HookBoardScrollViewer()
        {
            if (_boardScrollViewer != null) return true;
            if (BoardList.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject root) return false;

            _boardScrollViewer = FindScrollViewer(root);
            if (_boardScrollViewer is null) return false;

            _boardScrollViewer.ViewChanged -= OnBoardViewChanged;
            _boardScrollViewer.ViewChanged += OnBoardViewChanged;

            TuneNativeList();
            MaybeStartAutoFling();
            return true;
        }

        /// <summary>Fling-smoothness knobs on the native list. PROCURE_NO_TUNE=1 skips them so the
        /// fling rig can A/B one binary.</summary>
        private void TuneNativeList()
        {
            if (Environment.GetEnvironmentVariable("PROCURE_NO_TUNE") == "1")
            {
                Procure.Utilities.BoardTrace.Mark("native-tune skipped");
                return;
            }
            if (BoardList.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.ListViewBase lv) return;

            // Show placeholder containers during fast pans instead of blocking the UI thread to
            // realize full cards mid-fling; content fills in as the velocity drops.
            lv.ShowsScrollingPlaceholders = true;

            // Bound the off-screen realization buffer: the WinUI default realizes multiple extra
            // viewports of cache in the same pass that is already behind the fling.
            if (lv.ItemsPanelRoot is Microsoft.UI.Xaml.Controls.ItemsStackPanel panel)
            {
                panel.CacheLength = 1.0;
            }
            Procure.Utilities.BoardTrace.Mark("native-tune applied");
        }

        // Deterministic fling driver for A/B measurement: PROCURE_TRACE_FLING=<steps> sweeps the
        // board in fixed animated jumps on a fixed cadence; the trace heartbeat records every
        // UI-thread block it provokes. Diagnostics-only, inert without the env var.
        private bool _flingStarted;

        private void MaybeStartAutoFling()
        {
            if (_flingStarted) return;
            if (!int.TryParse(Environment.GetEnvironmentVariable("PROCURE_TRACE_FLING"), out var steps) || steps <= 0) return;
            _flingStarted = true;

            var step = 0;
            void Fling()
            {
                if (_boardScrollViewer is null || step >= steps)
                {
                    Procure.Utilities.BoardTrace.Mark("fling-done");
                    return;
                }
                step++;
                var target = _boardScrollViewer.VerticalOffset + 2600;
                if (Procure.Utilities.BoardTrace.IsEnabled)
                    Procure.Utilities.BoardTrace.Mark($"fling step={step} to={target:F0}");
                _boardScrollViewer.ChangeView(null, target, null);
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(900), Fling);
            }
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(2), Fling);
        }

        private static Microsoft.UI.Xaml.Controls.ScrollViewer? FindScrollViewer(Microsoft.UI.Xaml.DependencyObject node)
        {
            if (node is Microsoft.UI.Xaml.Controls.ScrollViewer sv) return sv;
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var found = FindScrollViewer(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
                if (found != null) return found;
            }
            return null;
        }

        private void OnBoardViewChanged(object? sender, Microsoft.UI.Xaml.Controls.ScrollViewerViewChangedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Controls.ScrollViewer sv) return;
            // Within two viewports of the end counts as "near the tail".
            var nearTail = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 2 * sv.ViewportHeight;
            _viewModel.OnBoardNearTail(nearTail);
        }
#endif

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
                    foreach (var status in ProcurementStatus.SelectableStatuses)
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
