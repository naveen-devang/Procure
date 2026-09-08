#if WINDOWS
using System;
using System.Diagnostics;
using X = Microsoft.UI.Xaml;
using XC = Microsoft.UI.Xaml.Controls;
using XM = Microsoft.UI.Xaml.Media;
using WinColor = Windows.UI.Color;

namespace Procure.Utilities
{
    /// <summary>
    /// On-screen frame-time / fps / worst-frame-per-second / last-page-load HUD. Kept
    /// visible in every migration build until cutover (standing request - see
    /// MIGRATION-PLAN.md 5a). Injected straight into the native window root so it
    /// survives page navigation and never touches the MAUI visual tree.
    /// </summary>
    internal static class PerfHud
    {
        private static XC.TextBlock? _label;
        private static long _lastTicks;
        private static double _emaMs;
        private static double _worstMs;
        private static DateTime _windowStart = DateTime.UtcNow;

        public static long LastPageLoadMs { get; set; }
        public static string LastPageLoadWhat { get; set; } = "";

        public static void ReportPageLoad(string what, long ms)
        {
            LastPageLoadWhat = what;
            LastPageLoadMs = ms;
        }

        public static void Attach(X.FrameworkElement anchor)
        {
            if (_label != null) return;

            if (FindRootPanel(anchor) is { } panel)
            {
                Mount(panel);
                return;
            }

            void Retry(object? s, object e)
            {
                if (FindRootPanel(anchor) is { } p)
                {
                    anchor.LayoutUpdated -= Retry;
                    Mount(p);
                }
            }
            anchor.LayoutUpdated += Retry;
        }

        private static void Mount(XC.Panel panel)
        {
            if (_label != null) return;

            _label = new XC.TextBlock
            {
                FontFamily = new XM.FontFamily("Consolas"),
                FontSize = 11,
                Foreground = new XM.SolidColorBrush(WinColor.FromArgb(0xFF, 0x30, 0xFF, 0x90)),
                Text = "perf: warming up…",
                IsHitTestVisible = false,
            };

            var host = new XC.Border
            {
                Background = new XM.SolidColorBrush(WinColor.FromArgb(0x99, 0x00, 0x00, 0x00)),
                CornerRadius = new X.CornerRadius(4),
                Padding = new X.Thickness(8, 4, 8, 4),
                IsHitTestVisible = false,
                HorizontalAlignment = X.HorizontalAlignment.Right,
                VerticalAlignment = X.VerticalAlignment.Top,
                Margin = new X.Thickness(0, 6, 10, 0),
                Child = _label,
            };

            XC.Canvas.SetZIndex(host, 10_000);
            panel.Children.Add(host);

            XM.CompositionTarget.Rendering += OnRendering;
        }

        private static XC.Panel? FindRootPanel(X.DependencyObject node)
        {
            X.DependencyObject? current = node;
            XC.Panel? top = null;
            while (current is not null)
            {
                if (current is XC.Panel p) top = p;
                current = XM.VisualTreeHelper.GetParent(current);
            }
            return top;
        }

        private static void OnRendering(object? sender, object e)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastTicks != 0 && _label != null)
            {
                var ms = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;
                _emaMs = _emaMs == 0 ? ms : _emaMs * 0.9 + ms * 0.1;
                if (ms > _worstMs) _worstMs = ms;

                if ((DateTime.UtcNow - _windowStart).TotalSeconds >= 1)
                {
                    var load = LastPageLoadMs > 0
                        ? $"   load {LastPageLoadMs} ms{(LastPageLoadWhat.Length > 0 ? $" ({LastPageLoadWhat})" : "")}"
                        : "";
                    _label.Text =
                        $"frame {_emaMs:F1} ms ({1000.0 / Math.Max(_emaMs, 0.01):F0} fps)   worst/1s {_worstMs:F1} ms{load}";
                    _worstMs = 0;
                    _windowStart = DateTime.UtcNow;
                }
            }
            _lastTicks = now;
        }
    }
}
#endif
