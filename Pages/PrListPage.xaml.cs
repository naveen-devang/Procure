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
    public partial class PrListPage : ContentPage
    {
        private readonly PrListPageModel _viewModel;
        private readonly Procure.Services.IKeyboardShortcutService _shortcuts;

        public PrListPage(PrListPageModel viewModel, Procure.Services.IKeyboardShortcutService shortcuts)
        {
            Procure.Utilities.BoardTrace.Mark("page-inflate-start");
            InitializeComponent();
            Procure.Utilities.BoardTrace.Mark("page-inflate-done");
            BindingContext = _viewModel = viewModel;
            _shortcuts = shortcuts;

            // Watch for IsBusy changes to start/stop the shimmer animation
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        // Whether the data is already warm, still loading, or untouched is the page model's business -
        // it owns the rows. This page only reports when the board can actually paint.
        protected override void OnAppearing()
        {
            base.OnAppearing();
#if WINDOWS
            Procure.Utilities.NativeTheme.ForceRepaintOnAppear(this);
#endif
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

            // Re-claim keyboard routing every time this page becomes the active one - switching back
            // from Dashboard/Settings/Columns leaves focus wherever that page last had it, not here.
            Dispatcher.Dispatch(FocusPageRootForKeyboard);
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
            FocusPageRootForKeyboard();
        }

        // WinUI only routes PreviewKeyDown along the path to whatever element currently holds logical
        // keyboard focus - with nothing focused (the state right after this page or any modal on it
        // first appears), no key reaches this hook at all, Escape and every shortcut included, until
        // the user happens to click or arrow-key something into focus first. Grabbing programmatic
        // focus on the page's own root the moment it's available closes that gap without needing a
        // visible focus target - it's never meant to look focused, only to give the input system
        // somewhere to route from.
        private void FocusPageRootForKeyboard()
        {
            if (Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement root) return;
            root.IsTabStop = true;
            root.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
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
            // Recording a new binding in Settings is handled at the Shell level, above this page in
            // the tunnel - nothing here needs to know or care while that's in progress.

            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (_viewModel.CloseTopmostModal()) e.Handled = true;
                return;
            }

            if (TryHandleModalScopedShortcut(e.Key)) { e.Handled = true; return; }

            // Board-level shortcuts only make sense when nothing is sitting on top of the board.
            if (_viewModel.IsAnyModalVisible) return;

            var s = _shortcuts;
            if (Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.FocusSearch), e.Key))
            {
                SearchEntry.Focus();
                e.Handled = true;
            }
            else if (Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.NewPr), e.Key))
            {
                _viewModel.OpenBatchCreateModalCommand.Execute(null);
                e.Handled = true;
            }
            else if (Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.RefreshBoard), e.Key))
            {
                _viewModel.RefreshBoardCommand.Execute(null);
                e.Handled = true;
            }
            else if (Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.ExportCsv), e.Key))
            {
                _viewModel.ExportCsvCommand.Execute(null);
                e.Handled = true;
            }
        }

        // Only ever acts on the one modal that's actually open - "Save" in a Batch Create dialog
        // must never also try to save an Edit PR dialog that happens to be visible at the same time
        // (it can't be, today, but the check costs nothing and keeps that true).
        private bool TryHandleModalScopedShortcut(Windows.System.VirtualKey key)
        {
            var s = _shortcuts;
            bool IsSave() => Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.ModalSave), key);
            bool IsSelectAll() => Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.ModalSelectAll), key);
            bool IsPaste() => Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.ModalPaste), key);

            // Ctrl+A/Ctrl+V and bare arrow keys all have a native meaning inside a focused text field
            // (select-all-text, paste-text, move-cursor) - stepping on that inside, say, the "Pages to
            // Print" box would be a regression, not a feature. Ctrl+S has no such native meaning in a
            // plain text box, so it's never guarded.
            var textFieldFocused = IsTextInputFocused();

            if (_viewModel.IsPcrPreviewVisible)
            {
                if (Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.PcrPrint), key))
                { _viewModel.PrintPcrPreviewCommand.Execute(null); return true; }
                if (IsSave())
                { _viewModel.SavePcrPreviewCommand.Execute(null); return true; }
                if (!textFieldFocused && _viewModel.IsPcrPagerVisible && Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.PcrPrevPage), key))
                { _viewModel.PreviousPcrPreviewPageCommand.Execute(null); return true; }
                if (!textFieldFocused && _viewModel.IsPcrPagerVisible && Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.PcrNextPage), key))
                { _viewModel.NextPcrPreviewPageCommand.Execute(null); return true; }
                if (Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.PcrZoomIn), key))
                { AdjustPcrZoom(0.1); return true; }
                if (Procure.Utilities.ShortcutInput.Matches(s.GetCombo(Procure.Utilities.KeyboardShortcutIds.PcrZoomOut), key))
                { AdjustPcrZoom(-0.1); return true; }
                return false;
            }

            if (IsSave())
            {
                if (_viewModel.IsEditModalVisible) { _viewModel.SavePrModalCommand.Execute(null); return true; }
                if (_viewModel.IsAddRfqModalVisible) { _viewModel.SaveNewRfqCommand.Execute(null); return true; }
                if (_viewModel.IsAddPoModalVisible) { _viewModel.SaveNewPoCommand.Execute(null); return true; }
                if (_viewModel.IsApprovalConfigModalVisible) { _viewModel.SaveApprovalConfigModalCommand.Execute(null); return true; }
                if (_viewModel.IsMergePrModalVisible) { _viewModel.ConfirmMergePrModalCommand.Execute(null); return true; }
                if (_viewModel.IsSplitPrModalVisible) { _viewModel.ConfirmSplitPrModalCommand.Execute(null); return true; }
                if (_viewModel.IsBatchCreateModalVisible) { _viewModel.SaveBatchPrsModalCommand.Execute(null); return true; }
                if (_viewModel.IsBatchRfqModalVisible) { _viewModel.SaveBatchRfqModalCommand.Execute(null); return true; }
                if (_viewModel.IsBatchPoModalVisible) { _viewModel.SaveBatchPoModalCommand.Execute(null); return true; }
                return false;
            }

            if (!textFieldFocused && IsSelectAll())
            {
                if (_viewModel.IsBatchRfqModalVisible) { _viewModel.SelectAllBatchRfqItemsCommand.Execute(null); return true; }
                if (_viewModel.IsAddPoModalVisible) { _viewModel.SelectAllPoRfqsCommand.Execute(null); return true; }
                return false;
            }

            if (!textFieldFocused && IsPaste() && _viewModel.IsBatchCreateModalVisible)
            {
                _viewModel.PasteBatchPrRowsFromClipboardCommand.Execute(null);
                return true;
            }

            return false;
        }

        // Whether a native text-editable control currently has keyboard focus - Ctrl+A/Ctrl+V and the
        // bare arrow keys all mean something different (and expected) inside one of those, so the
        // modal-scoped shortcuts that would otherwise collide with them back off while it's focused.
        private bool IsTextInputFocused()
        {
            if (Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement root || root.XamlRoot is null) return false;
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot);
            return focused is Microsoft.UI.Xaml.Controls.TextBox or Microsoft.UI.Xaml.Controls.PasswordBox or Microsoft.UI.Xaml.Controls.RichEditBox;
        }

        // Keyboard equivalent of Ctrl+scroll in the preview modal - resets pan too, same as every
        // other zoom-affecting change there (page switch, layout option change), so a keyboard zoom
        // never leaves the view panned somewhere the now-different zoom level can't show.
        private void AdjustPcrZoom(double step)
        {
            _viewModel.PcrPreviewZoom = Math.Clamp(Math.Round(_viewModel.PcrPreviewZoom + step, 2), 0.5, 2.5);
            _viewModel.PcrPreviewPanX = 0;
            _viewModel.PcrPreviewPanY = 0;
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

            // A modal just opened (or closed). Its content (Buttons, Pickers, Entries...) never
            // auto-claims focus, so without this, Escape and every other shortcut go dead the instant
            // it appears - exactly the PDF Preview modal report. Dispatched so the LazyExpander has
            // finished swapping its placeholder for the real content first. Harmless on close too -
            // it just hands keyboard routing back to the board.
            if (e.PropertyName != null &&
                (e.PropertyName.EndsWith("ModalVisible", StringComparison.Ordinal) ||
                 e.PropertyName == nameof(PrListPageModel.IsPcrPreviewVisible)))
            {
                Dispatcher.Dispatch(FocusPageRootForKeyboard);
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
            MaybeStartAutoExpand();
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

        // Deterministic expand driver for A/B memory measurement: PROCURE_TRACE_EXPAND=<n> expands the
        // first n currently-loaded cards' detail panels, spaced out so each gets its own frame, then
        // marks expand-done. Diagnostics-only, inert without the env var - same pattern as the fling
        // driver above.
        private bool _expandStarted;

        private void MaybeStartAutoExpand()
        {
            if (_expandStarted) return;
            if (!int.TryParse(Environment.GetEnvironmentVariable("PROCURE_TRACE_EXPAND"), out var count) || count <= 0) return;
            _expandStarted = true;

            var index = 0;
            void ExpandNext()
            {
                var rows = _viewModel.FilteredPrs;
                if (index >= count || index >= rows.Count)
                {
                    Procure.Utilities.BoardTrace.Mark($"expand-done n={index}");
                    return;
                }
                rows[index].IsExpanded = true;
                index++;
                if (Procure.Utilities.BoardTrace.IsEnabled)
                    Procure.Utilities.BoardTrace.Mark($"expand step={index}");
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(150), ExpandNext);
            }
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(1), ExpandNext);
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
    }
}
