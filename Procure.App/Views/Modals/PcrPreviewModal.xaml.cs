using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using Procure.PageModels;

namespace Procure.App.Views.Modals;

// The rendered-PDF preview. Zoom is Ctrl+wheel, pan is left-drag; both write straight to
// the page model's PcrPreviewZoom/PanX/PanY, which the CompositeTransform binds to (so a
// page change, which resets those, snaps the view back on its own).
public sealed partial class PcrPreviewModal : UserControl
{
    private PrListPageModel? Vm => DataContext as PrListPageModel ?? PrListPageModel.Current;

    private bool _dragging;
    private Windows.Foundation.Point _dragStart;
    private double _panStartX, _panStartY;

    public PcrPreviewModal()
    {
        InitializeComponent();
        PreviewViewport.PointerWheelChanged += OnWheel;
        PreviewViewport.PointerPressed += OnPointerPressed;
        PreviewViewport.PointerMoved += OnPointerMoved;
        PreviewViewport.PointerReleased += OnPointerReleased;
        PreviewViewport.PointerCanceled += OnPointerReleased;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (!ctrl || Vm is not { } vm) return;

        var delta = e.GetCurrentPoint(PreviewViewport).Properties.MouseWheelDelta;
        var next = Math.Clamp(vm.PcrPreviewZoom * (delta > 0 ? 1.1 : 1 / 1.1), 0.25, 6.0);
        vm.PcrPreviewZoom = next;
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _dragging = true;
        _dragStart = e.GetCurrentPoint(PreviewViewport).Position;
        _panStartX = vm.PcrPreviewPanX;
        _panStartY = vm.PcrPreviewPanY;
        PreviewViewport.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Vm is not { } vm) return;
        var p = e.GetCurrentPoint(PreviewViewport).Position;
        vm.PcrPreviewPanX = _panStartX + (p.X - _dragStart.X);
        vm.PcrPreviewPanY = _panStartY + (p.Y - _dragStart.Y);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        PreviewViewport.ReleasePointerCapture(e.Pointer);
    }

    // WinUI has no ClipToBounds; keep the scaled/panned sheet inside the viewport by hand.
    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PreviewViewport.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
        };
    }
}
