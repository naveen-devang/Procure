using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Procure.App.Views;

/// <summary>Minimal horizontal wrap panel - WinUI ships no WrapPanel and the detail panel's
/// RFQ micro-chip rows need one. Left-to-right, wrap on overflow, uniform row height.</summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 6;
    public double VerticalSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size available)
    {
        double lineW = 0, lineH = 0, totalW = 0, totalH = 0;
        var max = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (lineW > 0 && lineW + HorizontalSpacing + d.Width > max)
            {
                totalW = Math.Max(totalW, lineW);
                totalH += lineH + VerticalSpacing;
                lineW = 0; lineH = 0;
            }
            lineW += (lineW > 0 ? HorizontalSpacing : 0) + d.Width;
            lineH = Math.Max(lineH, d.Height);
        }
        totalW = Math.Max(totalW, lineW);
        totalH += lineH;
        return new Size(double.IsInfinity(available.Width) ? totalW : Math.Min(totalW, max), totalH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, lineH = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > final.Width)
            {
                x = 0; y += lineH + VerticalSpacing; lineH = 0;
            }
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width + HorizontalSpacing;
            lineH = Math.Max(lineH, d.Height);
        }
        return final;
    }
}
