using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Procure.App.Converters;
using Procure.Models;
using Procure.Utilities;

namespace Procure.App.Views;

/// <summary>
/// One item's prices over time: the market line (the middle of each month's quotes), shaded spikes
/// and notes, and dots - solid for a PO, a ring for a quote. Pointing at a dot shows who, how much
/// and when. Drawn straight onto a Canvas; redrawn when the data or the width changes, and emptied
/// when the data goes, so a closed chart holds nothing.
/// </summary>
public sealed class PriceChart : Grid
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(PriceChartData), typeof(PriceChart), new PropertyMetadata(null, (d, _) => ((PriceChart)d).Redraw()));

    public PriceChartData? Data
    {
        get => (PriceChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    private const double Left = 10, Right = 10, Top = 4, Bottom = 22;

    private readonly Grid _legend = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly Canvas _canvas = new();
    private readonly Border _tip;
    private readonly TextBlock _tipKind = new() { FontSize = 10.5, FontWeight = FontWeights.SemiBold, CharacterSpacing = 40 };
    private readonly TextBlock _tipVendor = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _tipPrice = new() { FontSize = 13 };
    private readonly TextBlock _tipWhen = new() { FontSize = 11.5 };
    private double _drawnWidth;

    // Where each dot was drawn, for the one pointer handler below. A handler per dot handed a
    // delegate per dot to WinRT - up to 1,200 a chart - and .NET 10.0.12 never lets those go.
    private readonly List<(double X, double Y, ChartDot Dot)> _hits = new();

    public PriceChart()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(_legend);
        SetRow(_canvas, 1);
        Children.Add(_canvas);

        // Dark in both themes, like a system tooltip.
        var light = BoardTheme.Brush("#FFFFFF");
        var soft = BoardTheme.Brush("#B8B2A9");
        _tipVendor.Foreground = light;
        _tipPrice.Foreground = light;
        _tipWhen.Foreground = soft;
        _tip = new Border
        {
            Background = BoardTheme.Pick("#3B3A38", "#1F1E1C"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Width = 200,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new StackPanel { Spacing = 3, Children = { _tipKind, _tipVendor, _tipPrice, _tipWhen } },
        };
        Canvas.SetZIndex(_tip, 10);

        _canvas.Background = BoardTheme.Brush("#00FFFFFF");   // hit-testable, so the pointer is seen between dots
        _canvas.PointerMoved += (_, e) =>
        {
            var p = e.GetCurrentPoint(_canvas).Position;
            (double X, double Y, ChartDot Dot)? near = null;
            var best = 64.0;   // within 8 px
            foreach (var h in _hits)
            {
                var d = (h.X - p.X) * (h.X - p.X) + (h.Y - p.Y) * (h.Y - p.Y);
                if (d <= best) { best = d; near = h; }
            }
            if (near is { } n && Data is { } data) ShowTip(n.Dot, data, n.X, n.Y, _canvas.ActualWidth);
            else _tip.Visibility = Visibility.Collapsed;
        };
        _canvas.PointerExited += (_, _) => _tip.Visibility = Visibility.Collapsed;

        // The drawing area's own size, known only after layout: the legend above it takes its share.
        _canvas.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _drawnWidth) > 1 || e.PreviousSize.Height != e.NewSize.Height) Redraw();
        };
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        _legend.Children.Clear();
        _hits.Clear();
        _tip.Visibility = Visibility.Collapsed;

        var data = Data;
        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        _drawnWidth = width;
        if (data is null || width < 120 || height < 60) return;

        var text = BoardTheme.Themed("AppTextPrimary");
        var faint = BoardTheme.Themed("AppTextTertiary");
        var rule = BoardTheme.Themed("AppControlBorder");
        var accent = BoardTheme.Themed("AccentFillColorDefaultBrush");
        var paper = BoardTheme.Themed("AppSecondaryBackground");
        var ring = data.GreyQuotes ? faint : accent;
        var itemView = data.ShowHint;

        // Legend, as in the design: the market line, a solid dot for an order, a ring for a quote.
        _legend.ColumnDefinitions.Clear();
        _legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _legend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = itemView ? 16 : 14 };
        keys.Children.Add(LegendItem(new Rectangle { Width = 18, Height = 3, Fill = text }, data.MarketLegend));
        keys.Children.Add(LegendItem(new Ellipse { Width = 10, Height = 10, Fill = accent }, data.PoLegend));
        keys.Children.Add(LegendItem(new Ellipse { Width = 10, Height = 10, Fill = paper, Stroke = ring, StrokeThickness = 2 }, data.QuoteLegend));
        _legend.Children.Add(keys);
        if (itemView)
        {
            var hint = new TextBlock
            {
                Text = "Point at a dot for the supplier and unit price",
                FontSize = 11.5,
                Foreground = BoardTheme.Themed("AppTextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(hint, 1);
            _legend.Children.Add(hint);
        }

        if (data.IsEmpty)
        {
            _canvas.Children.Add(Label("No prices in this period", faint, 12, Left, height / 2 - 10));
            return;
        }

        var months = data.ToMonth - data.FromMonth + 1;
        var plotW = width - Left - Right;
        var plotH = height - Top - Bottom;
        double X(double month) => Left + (month - data.FromMonth) / months * plotW;

        var values = data.Market.Select(m => m.Value).Concat(data.Dots.Select(d => d.Price)).ToList();
        var lo = values.Min();
        var hi = values.Max();
        var pad = Math.Max((hi - lo) * 0.12, hi * 0.02 + 0.01);
        lo = Math.Max(0, lo - pad);
        hi += pad;
        // Room above the highest point for the spike captions.
        var captionRoom = data.Bands.Count > 0 ? 40 : 6;
        double Y(double price) => Top + captionRoom + (hi - price) / (hi - lo) * (plotH - captionRoom);

        // Spikes and notes behind everything else, then every caption on top of the shading. A caption
        // takes the first of two lanes free at its left edge; with none free it is left off.
        var bands = data.Bands.OrderBy(b => b.FromMonth).ToList();
        foreach (var band in bands)
        {
            var x1 = X(Math.Max(band.FromMonth, data.FromMonth));
            var x2 = X(Math.Min(band.ToMonth, data.ToMonth) + 1);
            var rect = new Rectangle
            {
                Width = Math.Max(2, x2 - x1),
                Height = plotH,
                Fill = band.IsSpike ? BoardTheme.Themed("FluentCautionBg") : BoardTheme.Themed("AppSubtleFill"),
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, Top);
            _canvas.Children.Add(rect);
        }
        var laneEnds = new[] { double.MinValue, double.MinValue };
        foreach (var band in bands)
        {
            var x1 = X(Math.Max(band.FromMonth, data.FromMonth));
            var ink = band.IsSpike ? BoardTheme.Themed("FluentCaution") : BoardTheme.Themed("AppTextSecondary");
            var caption = new StackPanel { Spacing = 2, MaxWidth = itemView ? 320 : 230 };
            if (band.Title.Length > 0)
                caption.Children.Add(new TextBlock { Text = band.Title, FontSize = itemView ? 12 : 11, FontWeight = FontWeights.SemiBold, Foreground = ink });
            if (band.Note.Length > 0)
                caption.Children.Add(new TextBlock
                {
                    Text = (itemView ? "Your note: " : "Note: ") + band.Note,
                    FontSize = itemView ? 11.5 : 10.5,
                    Foreground = ink,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                });
            caption.Measure(new Windows.Foundation.Size(caption.MaxWidth, double.PositiveInfinity));
            var left = Math.Min(x1 + (itemView ? 10 : 6), width - Right - caption.DesiredSize.Width);
            var lane = Array.FindIndex(laneEnds, end => left > end + 8);
            if (lane < 0) continue;
            laneEnds[lane] = left + caption.DesiredSize.Width;
            Canvas.SetLeft(caption, left);
            Canvas.SetTop(caption, Top + 6 + lane * 34);
            _canvas.Children.Add(caption);
        }

        // One baseline, no grid: the dots carry their prices on hover.
        var baseY = height - Bottom;
        _canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = baseY, Y2 = baseY, Stroke = rule, StrokeThickness = 1 });

        // Month labels, at most about eight.
        var step = Math.Max(1, (int)Math.Ceiling(months / 8.0));
        for (var m = data.FromMonth; m <= data.ToMonth; m += step)
        {
            var start = MonthIndex.Start(m);
            var name = m == data.FromMonth || start.Month == 1 ? start.ToString("MMM yy", CultureInfo.CurrentCulture) : MonthIndex.Short(m);
            var label = Label(name, faint, 10.5, X(m + 0.5) - 30, baseY + 6);
            label.Width = 60;
            label.TextAlignment = TextAlignment.Center;
            _canvas.Children.Add(label);
        }

        // The market line, through the middle of each month.
        if (data.Market.Count > 0)
        {
            var line = new Polyline { Stroke = text, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round };
            foreach (var (month, value) in data.Market) line.Points.Add(new Windows.Foundation.Point(X(month + 0.5), Y(value)));
            _canvas.Children.Add(line);
        }

        // Quotes first, orders on top of them.
        foreach (var dot in data.Dots.Where(d => !d.IsPo).Concat(data.Dots.Where(d => d.IsPo)))
        {
            var size = dot.IsPo ? 14.0 : 11.0;
            var e = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = dot.IsPo ? accent : paper,
                Stroke = dot.IsPo ? paper : ring,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            var cx = X(dot.X);
            var cy = Y(dot.Price);
            Canvas.SetLeft(e, cx - size / 2);
            Canvas.SetTop(e, cy - size / 2);
            _hits.Add((cx, cy, dot));
            _canvas.Children.Add(e);
        }

        _canvas.Children.Add(_tip);
    }

    private void ShowTip(ChartDot dot, PriceChartData data, double x, double y, double width)
    {
        _tipKind.Text = dot.IsPo ? "BOUGHT (PO)" : "QUOTE";
        _tipKind.Foreground = dot.IsPo ? BoardTheme.Brush("#8CC8FF") : BoardTheme.Brush("#B8B2A9");
        _tipVendor.Text = dot.Vendor;
        _tipPrice.Inlines.Clear();
        _tipPrice.Inlines.Add(new Run { Text = PriceAnalysis.Money(data.Currency, dot.Price) + " " });
        _tipPrice.Inlines.Add(new Run { Text = "per " + data.Unit, Foreground = BoardTheme.Brush("#B8B2A9") });
        _tipWhen.Text = dot.Ref.Length > 0 ? $"{dot.When} · {dot.Ref}" : dot.When;
        Canvas.SetLeft(_tip, x + 14 + _tip.Width > width ? x - 14 - _tip.Width : x + 14);
        Canvas.SetTop(_tip, Math.Max(0, y - 40));
        _tip.Visibility = Visibility.Visible;
    }

    private static TextBlock Label(string text, Brush brush, double size, double x, double y)
    {
        var t = new TextBlock { Text = text, FontSize = size, Foreground = brush, IsHitTestVisible = false };
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        return t;
    }

    private static StackPanel LegendItem(FrameworkElement swatch, string label)
    {
        swatch.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                swatch,
                new TextBlock { Text = label, FontSize = 11.5, Foreground = BoardTheme.Themed("AppTextSecondary"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }
}
