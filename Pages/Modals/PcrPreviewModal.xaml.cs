using System;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Procure.PageModels;

namespace Procure.Pages.Modals
{
    public partial class PcrPreviewModal : ContentView
    {
        private FrameworkElement? _hookedRoot;
        private double _panStartX;
        private double _panStartY;

        public PcrPreviewModal()
        {
            InitializeComponent();
            PreviewViewport.HandlerChanged += OnPreviewViewportHandlerChanged;

            // Border's own shape-clip doesn't reach a Scale-transformed grandchild on this MAUI/WinUI
            // version - zoomed-in content rendered straight past its edges. An explicit Clip geometry
            // sized to the host's own bounds clips the whole subtree regardless of how deep the render
            // transform sits, so it's re-applied every time the pane resizes.
            PreviewClipHost.SizeChanged += (_, _) =>
            {
                if (PreviewClipHost.Width > 0 && PreviewClipHost.Height > 0)
                {
                    PreviewClipHost.Clip = new RectangleGeometry
                    {
                        Rect = new Rect(0, 0, PreviewClipHost.Width, PreviewClipHost.Height)
                    };
                }
            };
        }

        // Ctrl+scroll zoom needs the wheel delta and key-modifier state MAUI doesn't expose, so this
        // hooks the native element directly - same approach as the board's native ScrollViewer hook in
        // PrListPage.xaml.cs. Hooked on the whole viewport (not just the page image) so the gesture
        // works anywhere over the preview pane. Plain `+=` is safe here (no handledEventsToo dance
        // needed) because the viewport no longer contains a ScrollView to compete with for the event -
        // see the XAML comment on why that combination could hang the app.
        private void OnPreviewViewportHandlerChanged(object? sender, EventArgs e)
        {
            if (_hookedRoot != null)
            {
                _hookedRoot.PointerWheelChanged -= OnPreviewPointerWheelChanged;
                _hookedRoot = null;
            }

            if (PreviewViewport.Handler?.PlatformView is FrameworkElement root)
            {
                root.PointerWheelChanged += OnPreviewPointerWheelChanged;
                _hookedRoot = root;
            }
        }

        private void OnPreviewPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (BindingContext is not PrListPageModel vm) return;
            var properties = e.GetCurrentPoint((UIElement)sender).Properties;

            if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
            {
                var step = properties.MouseWheelDelta > 0 ? 0.1 : -0.1;
                vm.PcrPreviewZoom = Math.Clamp(Math.Round(vm.PcrPreviewZoom + step, 2), 0.5, 2.5);
                ClampPan(vm);
                e.Handled = true;
                return;
            }

            // Plain wheel pans instead of scrolling - there's nothing else to scroll now that the
            // viewport isn't a ScrollView. A trackpad's two-finger swipe arrives as a horizontal wheel
            // delta on Windows (IsHorizontalMouseWheel), not as a drag gesture, so PanGestureRecognizer
            // alone never saw it - this is what made horizontal movement mouse-only before.
            var panDelta = properties.MouseWheelDelta / 120.0 * 40.0;
            if (properties.IsHorizontalMouseWheel)
            {
                // Windows reports a trackpad's horizontal swipe with the opposite sign convention to
                // its vertical one relative to natural-scroll feel - negate so a right-swipe pans the
                // content the same intuitive direction a down-swipe already does vertically.
                vm.PcrPreviewPanX -= panDelta;
            }
            else
            {
                vm.PcrPreviewPanY += panDelta;
            }
            ClampPan(vm);
            e.Handled = true;
        }

        // Drag-to-pan with a mouse (click and drag) - the wheel-based path above covers trackpad
        // swipes, which arrive as synthetic wheel events rather than a drag gesture on Windows.
        private void OnPreviewPanUpdated(object? sender, PanUpdatedEventArgs e)
        {
            if (BindingContext is not PrListPageModel vm) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _panStartX = vm.PcrPreviewPanX;
                    _panStartY = vm.PcrPreviewPanY;
                    break;
                case GestureStatus.Running:
                    vm.PcrPreviewPanX = _panStartX + e.TotalX;
                    vm.PcrPreviewPanY = _panStartY + e.TotalY;
                    ClampPan(vm);
                    break;
            }
        }

        // Keeps the page anchored to the viewport instead of letting it drift off into blank space:
        // pan is bounded to however far the zoomed content actually overhangs the viewport on each
        // axis, and collapses to zero once the content is no larger than the viewport on that axis.
        private void ClampPan(PrListPageModel vm)
        {
            var viewportWidth = PreviewClipHost.Width;
            var viewportHeight = PreviewClipHost.Height;
            if (viewportWidth <= 0 || viewportHeight <= 0) return;

            var contentWidth = PreviewSheetFrame.Width * vm.PcrPreviewZoom;
            var contentHeight = PreviewSheetFrame.Height * vm.PcrPreviewZoom;

            var maxPanX = Math.Max(0, (contentWidth - viewportWidth) / 2);
            var maxPanY = Math.Max(0, (contentHeight - viewportHeight) / 2);

            vm.PcrPreviewPanX = Math.Clamp(vm.PcrPreviewPanX, -maxPanX, maxPanX);
            vm.PcrPreviewPanY = Math.Clamp(vm.PcrPreviewPanY, -maxPanY, maxPanY);
        }
    }
}
