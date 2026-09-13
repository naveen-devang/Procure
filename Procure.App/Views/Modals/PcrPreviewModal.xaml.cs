using System;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
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

    // Sharp zoom (see RenderDetailTileAsync). Held as a field: a timer that is only a local can be
    // collected before it fires.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _detailTimer;
    private PrListPageModel? _watched;
    private int _detailGeneration;

    public PcrPreviewModal()
    {
        InitializeComponent();
        PreviewViewport.PointerWheelChanged += OnWheel;
        PreviewViewport.PointerPressed += OnPointerPressed;
        PreviewViewport.PointerMoved += OnPointerMoved;
        PreviewViewport.PointerReleased += OnPointerReleased;
        PreviewViewport.PointerCanceled += OnPointerReleased;

        _detailTimer = DispatcherQueue.CreateTimer();
        _detailTimer.Interval = TimeSpan.FromMilliseconds(150);
        _detailTimer.IsRepeating = false;
        _detailTimer.Tick += (_, _) => _ = RenderDetailTileAsync();

        Loaded += (_, _) =>
        {
            Watch(Vm);
            // Dragged to a screen with different scaling: the sharp tile was drawn for the old one.
            if (XamlRoot != null) XamlRoot.Changed += OnXamlRootChanged;
        };
        Unloaded += (_, _) =>
        {
            Watch(null);
            if (XamlRoot != null) XamlRoot.Changed -= OnXamlRootChanged;
            ClearDetailTile();
        };
        DataContextChanged += (_, _) => { if (IsLoaded) Watch(Vm); };
    }

    private double _lastRasterizationScale;

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale == _lastRasterizationScale) return;
        _lastRasterizationScale = sender.RasterizationScale;
        ScheduleDetailTile();
    }

    private void Watch(PrListPageModel? vm)
    {
        if (ReferenceEquals(vm, _watched)) return;
        if (_watched != null) _watched.PropertyChanged -= OnVmPropertyChanged;
        _watched = vm;
        if (vm != null) vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PrListPageModel.PcrPreviewZoom):
            case nameof(PrListPageModel.PcrPreviewPanX):
            case nameof(PrListPageModel.PcrPreviewPanY):
            case nameof(PrListPageModel.PcrPreviewCurrentPage):
            case nameof(PrListPageModel.PcrPreviewPageIndex):
            case nameof(PrListPageModel.IsPcrPreviewVisible):
                ScheduleDetailTile();
                break;
        }
    }

    // The view is moving: the sharp tile no longer lines up, so hide it at once and draw a new one
    // once things have been still for a moment - the stretched page image covers the gap, the way
    // Acrobat sharpens a page a beat after you stop zooming.
    private void ScheduleDetailTile()
    {
        ClearDetailTile();
        _detailTimer.Stop();
        _detailTimer.Start();
    }

    private void ClearDetailTile()
    {
        _detailGeneration++;
        DetailTile.Visibility = Visibility.Collapsed;
        DetailTile.Source = null;   // one viewport of pixels, released as soon as it is out of date
    }

    /// <summary>
    /// Draws the part of the page that is on screen again, straight from the PDF, at the screen's own
    /// resolution, and lays it exactly over the stretched page image.
    ///
    /// The page image is rendered once at the preview DPI. At normal zoom it already has more pixels
    /// than the screen shows, so nothing extra is made - a five-supplier sheet at 100% never draws a
    /// tile. Zoomed in past that, stretching it blurs small text badly (a sheet fitted for twelve
    /// suppliers prints at ~4.9 pt). The tile is only ever the visible region, so it costs one
    /// viewport of pixels whatever the zoom, and it is dropped the moment the view changes.
    /// </summary>
    private async System.Threading.Tasks.Task RenderDetailTileAsync()
    {
        var vm = Vm;
        var source = vm?.PcrPreviewSource;
        if (vm == null || source == null || !vm.IsPcrPreviewVisible || vm.PcrPreviewCurrentPage == null || XamlRoot == null) return;

        var pageIndex = vm.PcrPreviewPageIndex;
        if (pageIndex < 0 || pageIndex >= source.PageCount) return;
        if (PageImage.ActualWidth <= 0 || PageImage.ActualHeight <= 0) return;

        // Where the page is on screen, zoom and pan included.
        var pageRect = PageImage.TransformToVisual(PreviewViewport)
            .TransformBounds(new Rect(0, 0, PageImage.ActualWidth, PageImage.ActualHeight));
        var viewport = new Rect(0, 0, PreviewViewport.ActualWidth, PreviewViewport.ActualHeight);
        var visible = pageRect;
        visible.Intersect(viewport);
        if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1) return;

        var screenScale = XamlRoot.RasterizationScale;
        _lastRasterizationScale = screenScale;
        if (source.IsDisposed) return;
        var (pageWidthDips, pageHeightDips) = source.PageSizeDips(pageIndex);
        double imagePixelsWide = pageWidthDips * source.EffectiveDpi / 96.0;
        double screenPixelsWide = pageRect.Width * screenScale;
        if (screenPixelsWide <= imagePixelsWide * 1.05) return;   // the page image is already sharp enough here

        // The visible region in the page's own coordinates.
        var sourceRect = new Rect(
            (visible.X - pageRect.X) / pageRect.Width * pageWidthDips,
            (visible.Y - pageRect.Y) / pageRect.Height * pageHeightDips,
            visible.Width / pageRect.Width * pageWidthDips,
            visible.Height / pageRect.Height * pageHeightDips);

        var generation = ++_detailGeneration;
        try
        {
            using var stream = await source.RenderRegionAsync(pageIndex, sourceRect,
                (uint)Math.Ceiling(visible.Width * screenScale), (uint)Math.Ceiling(visible.Height * screenScale));
            if (generation != _detailGeneration) return;   // the view moved while it drew

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (generation != _detailGeneration) return;

            DetailTile.Source = bitmap;
            DetailTile.Margin = new Thickness(visible.X, visible.Y, 0, 0);
            DetailTile.Width = visible.Width;
            DetailTile.Height = visible.Height;
            DetailTile.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // A document replaced (and released) by an option change mid-draw is expected, not a fault.
            if (source.IsDisposed || generation != _detailGeneration) return;
            // A failed sharpen leaves the stretched image showing, which is still a working preview.
            Procure.Utilities.CrashLog.Write("PCR preview detail render failed", ex);
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (Vm is not { } vm) return;

        if (!ctrl)
        {
            // Plain wheel pans up/down; a trackpad's sideways swipe arrives as a horizontal wheel and pans
            // left/right (as in the MAUI preview). 40 px per notch.
            var props = e.GetCurrentPoint(PreviewViewport).Properties;
            var step = props.MouseWheelDelta / 120.0 * 40.0;
            if (props.IsHorizontalMouseWheel) vm.PcrPreviewPanX -= step;
            else vm.PcrPreviewPanY += step;
            ClampPan(vm, vm.PcrPreviewZoom);
            e.Handled = true;
            return;
        }

        var delta = e.GetCurrentPoint(PreviewViewport).Properties.MouseWheelDelta;
        var next = Math.Clamp(vm.PcrPreviewZoom * (delta > 0 ? 1.1 : 1 / 1.1), 0.25, 6.0);
        vm.PcrPreviewZoom = next;
        ClampPan(vm, next);
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
        ClampPan(vm, vm.PcrPreviewZoom);
    }

    /// <summary>Keeps the page on screen: it can move only as far as it overhangs the viewport on each side,
    /// and not at all along a side where it fits. Otherwise a drag or wheel could leave the viewport empty.</summary>
    private void ClampPan(PrListPageModel vm, double zoom)
    {
        var viewW = PreviewViewport.ActualWidth;
        var viewH = PreviewViewport.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        var maxX = Math.Max(0, (SheetFrame.ActualWidth * zoom - viewW) / 2);
        var maxY = Math.Max(0, (SheetFrame.ActualHeight * zoom - viewH) / 2);
        var x = Math.Clamp(vm.PcrPreviewPanX, -maxX, maxX);
        var y = Math.Clamp(vm.PcrPreviewPanY, -maxY, maxY);
        if (x != vm.PcrPreviewPanX) vm.PcrPreviewPanX = x;
        if (y != vm.PcrPreviewPanY) vm.PcrPreviewPanY = y;
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
        ScheduleDetailTile();
    }
}
