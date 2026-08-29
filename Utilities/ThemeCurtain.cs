#if WINDOWS
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace Procure.Utilities
{
    /// <summary>
    /// One curtain for the whole window, laid over everything - sidebar included - for the length of
    /// a theme switch. Replaces the per-page BoxView curtains, which left the sidebar and any page
    /// without one (Settings, Materials) to hard-flip in plain sight.
    ///
    /// The fade runs as a WinUI Storyboard on Opacity: an independent animation, so it plays on the
    /// compositor thread regardless of what the UI thread is doing. That matters because applying a
    /// theme re-evaluates every AppThemeBinding across all five singleton pages at once - measured at
    /// 124-149ms of UI-thread stall, plus ~100ms of card repaint queued behind it. The old MAUI
    /// FadeTo reveal was queued after a fixed 40ms, landed inside that stall, and was handed the whole
    /// stall as elapsed time by the frame ticker: a 200ms fade that measured 13ms - a hard cut.
    /// </summary>
    internal static class ThemeCurtain
    {
        private static readonly Windows.UI.Color Dark = Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20);
        private static readonly Windows.UI.Color Light = Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);

        public const int FadeInMs = 160;
        public const int FadeOutMs = 200;

        // Test seams for ThemeTransitionSelfCheck.
        internal static Windows.Foundation.Size LastCurtainSizeForTest { get; private set; }
        internal static Windows.Foundation.Size LastRootSizeForTest { get; private set; }
        internal static double OpacityAtApplyForTest { get; private set; }
        internal static long LastRevealMsForTest { get; private set; }
        internal static bool LastSheetIsDarkForTest { get; private set; }
        internal static readonly List<Storyboard> StoryboardsForTest = new();

        /// <summary>Fades the curtain in, runs <paramref name="applyTheme"/> behind it, waits for the
        /// new theme to be laid out and drawn once underneath, then fades the curtain out.</summary>
        public static async Task RunAsync(Microsoft.UI.Xaml.Window window, bool toDark, Action applyTheme)
        {
            if (window.Content is not FrameworkElement root || root.XamlRoot is null)
            {
                applyTheme();
                return;
            }

            var xamlRoot = root.XamlRoot;
            var rect = new Rectangle
            {
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(toDark ? Dark : Light),
                Opacity = 0,
                IsHitTestVisible = false,
                Width = xamlRoot.Size.Width,
                Height = xamlRoot.Size.Height
            };
            var popup = new Popup
            {
                XamlRoot = xamlRoot,
                Child = rect,
                IsHitTestVisible = false,
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = true
            };

            LastRootSizeForTest = xamlRoot.Size;
            LastSheetIsDarkForTest = toDark;
            StoryboardsForTest.Clear();
            LastCurtainSizeForTest = new Windows.Foundation.Size(rect.Width, rect.Height);

            try
            {
                popup.IsOpen = true;
                // The title bar cannot be covered (caption buttons are OS chrome, and MAUI's title
                // text never follows the theme on its own) - so it is animated in lockstep instead.
                await Task.WhenAll(FadeAsync(rect, 1.0, FadeInMs), TitleBarHelper.FadeIntoSheetAsync(toDark, FadeInMs));

                OpacityAtApplyForTest = rect.Opacity;
                applyTheme();

                // Everything the apply queued (card repaints, highlight fades) runs at Normal priority;
                // Low runs only once those have drained AND the resulting layout/render pass has
                // happened. So the new theme has been drawn once, under the curtain, before it lifts.
                await LowPriorityTickAsync(window.DispatcherQueue);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Task.WhenAll(FadeAsync(rect, 0.0, FadeOutMs), TitleBarHelper.EmergeFromSheetAsync(toDark, FadeOutMs));
                LastRevealMsForTest = sw.ElapsedMilliseconds;
            }
            finally
            {
                popup.IsOpen = false;
                TitleBarHelper.EndTransition();
            }
        }

        private static Task FadeAsync(UIElement target, double to, int ms)
        {
            var tcs = new TaskCompletionSource();
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(anim);
            // WinUI animations HoldEnd by default: a finished animation keeps sitting on top of the
            // property, so any value assigned afterwards is stored but never displayed. Commit the end
            // value as the real value, then stop the animation so it lets go.
            storyboard.Completed += (_, _) => { target.Opacity = to; storyboard.Stop(); tcs.TrySetResult(); };
            StoryboardsForTest.Add(storyboard);
            storyboard.Begin();
            return tcs.Task;
        }

        private static Task LowPriorityTickAsync(DispatcherQueue queue)
        {
            var tcs = new TaskCompletionSource();
            if (!queue.TryEnqueue(DispatcherQueuePriority.Low, () => tcs.TrySetResult())) tcs.TrySetResult();
            return tcs.Task;
        }

        /// <summary>Masks a single page reveal with a brief opaque cover in the current theme's sheet
        /// colour, opened BEFORE the page is shown (call from OnNavigating, before Shell swaps content)
        /// and held ~150ms before fading. A page navigated to after the app's theme changed can show a
        /// visibly wrong frame for an instant even though every colour underneath it is already correct
        /// - a native repaint hint (see NativeTheme.ForceRepaintOnAppear) was tried and did not clear it
        /// for every user, and diagnosing the exact WinUI mechanism further was not converging. This
        /// does not depend on diagnosing it: nothing wrong can be seen under an opaque cover, regardless
        /// of cause. Fire-and-forget from OnNavigating is safe - Popup.IsOpen takes effect before the
        /// caller's own synchronous return, so it is up before Shell's content swap paints anything.</summary>
        internal static Task? LastMaskTaskForTest { get; set; }

        public static Task MaskPageRevealAsync(Microsoft.UI.Xaml.Window window, bool isDark, int holdMs = 150, int fadeMs = 120)
        {
            var task = MaskPageRevealCoreAsync(window, isDark, holdMs, fadeMs);
            LastMaskTaskForTest = task;
            return task;
        }

        private static async Task MaskPageRevealCoreAsync(Microsoft.UI.Xaml.Window window, bool isDark, int holdMs, int fadeMs)
        {
            if (window.Content is not FrameworkElement root || root.XamlRoot is null) return;
            var xamlRoot = root.XamlRoot;

            var rect = new Rectangle
            {
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(isDark ? Dark : Light),
                Opacity = 1.0, // opaque immediately - no fade-in, the reveal itself must never be seen
                IsHitTestVisible = false,
                Width = xamlRoot.Size.Width,
                Height = xamlRoot.Size.Height
            };
            var popup = new Popup
            {
                XamlRoot = xamlRoot,
                Child = rect,
                IsHitTestVisible = false,
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = true
            };

            try
            {
                popup.IsOpen = true;
                await Task.Delay(holdMs);
                await FadeAsync(rect, 0.0, fadeMs);
            }
            finally
            {
                popup.IsOpen = false;
            }
        }
    }
}
#endif
