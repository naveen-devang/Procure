using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Procure.Services;

namespace Procure.App.Views;

/// <summary>Widths of the Service Entries table's fixed columns. The heading row and every table row
/// x:Bind their ColumnDefinitions to <see cref="Current"/>, so a drag moves them all together.
/// Vendor / Description is not in here: it is the star column that takes what is left.
/// Saved to settings.json (per computer, not in the shared database) when a drag ends.</summary>
public sealed partial class ServiceEntryColumns : ObservableObject
{
    // Sr, PO No, PO Amount, | Vendor (star) |, Inv No/Date, Inv Amount, Tech, SE No/SAP date, Status
    private static readonly double[] Defaults = { 48, 104, 90, 120, 90, 66, 104, 124 };
    private static readonly double[] Mins = { 36, 70, 64, 80, 64, 56, 72, 110 };
    private const double Max = 600, VendorMin = 150, Chrome = 40;   // row padding + list scrollbar

    // After the arrays above: static fields initialise in order, and the constructor copies Defaults.
    public static ServiceEntryColumns Current { get; } = new();

    private readonly double[] _w = (double[])Defaults.Clone();
    private ISettingsService? _settings;

    public GridLength C0 => new(_w[0]);
    public GridLength C1 => new(_w[1]);
    public GridLength C2 => new(_w[2]);
    public GridLength C3 => new(_w[3]);
    public GridLength C4 => new(_w[4]);
    public GridLength C5 => new(_w[5]);
    public GridLength C6 => new(_w[6]);
    public GridLength C7 => new(_w[7]);

    /// <summary>The narrowest the table can be before it scrolls sideways.</summary>
    public double TableMinWidth => _w.Sum() + VendorMin + Chrome;

    public double Width(int i) => _w[i];

    public void Load(ISettingsService settings)
    {
        if (_settings != null) return;
        _settings = settings;
        var parts = (settings.ServiceEntryColumnWidths ?? "").Split(',');
        for (var i = 0; i < _w.Length && i < parts.Length; i++)
            if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w >= Mins[i] && w <= Max)
                _w[i] = w;   // anything missing or out of range keeps its default
        RaiseAll();
    }

    public void Set(int i, double width)
    {
        var w = Math.Round(Math.Clamp(width, Mins[i], Max));
        if (w == _w[i]) return;
        _w[i] = w;
        OnPropertyChanged("C" + i);
        OnPropertyChanged(nameof(TableMinWidth));
    }

    public void Reset(int i) { Set(i, Defaults[i]); Save(); }

    public void ResetAll()
    {
        Array.Copy(Defaults, _w, _w.Length);
        RaiseAll();
        Save();
    }

    public void Save()
    {
        if (_settings is null) return;
        _settings.ServiceEntryColumnWidths = string.Join(",", _w.Select(w => w.ToString(CultureInfo.InvariantCulture)));
    }

    private void RaiseAll() => OnPropertyChanged(string.Empty);
}

/// <summary>The drag handle on a heading's right edge. Drag to resize column <see cref="Column"/>,
/// double-click to put it back. A Grid subclass only because the resize cursor is a protected member.</summary>
public sealed partial class ColumnResizeGrip : Grid
{
    public int Column { get; set; }

    private double _startX, _startWidth;
    private bool _dragging;
    private readonly Border _line;

    public ColumnResizeGrip()
    {
        Width = 10;
        Margin = new Thickness(0, -4, -5, -4);
        HorizontalAlignment = HorizontalAlignment.Right;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);   // hit-testable
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        ToolTipService.SetToolTip(this, "Drag to resize. Double-click to reset.");

        _line = new Border { Width = 2, CornerRadius = new CornerRadius(1), Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center };
        _line.SetValue(Border.BackgroundProperty, Application.Current.Resources["AccentFillBrush"]);
        Children.Add(_line);

        PointerEntered += (_, _) => _line.Opacity = 1;
        PointerExited += (_, _) => { if (!_dragging) _line.Opacity = 0; };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        DoubleTapped += (_, e) => { ServiceEntryColumns.Current.Reset(Column); e.Handled = true; };
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        _startX = e.GetCurrentPoint(null).Position.X;
        _startWidth = ServiceEntryColumns.Current.Width(Column);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ServiceEntryColumns.Current.Set(Column, _startWidth + e.GetCurrentPoint(null).Position.X - _startX);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _line.Opacity = 0;
        ServiceEntryColumns.Current.Save();   // once per drag, not per pixel
    }
}
