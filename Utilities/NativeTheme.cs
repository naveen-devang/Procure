#if WINDOWS
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml;

namespace Procure.Utilities
{
    /// <summary>
    /// The one answer to "which theme are we going to" and the one place that pushes it into WinUI.
    ///
    /// Resolving: "Dark"/"Light" are themselves; "System" is whatever Windows is right now. The curtain
    /// and the title bar used to decide with <c>target == "Dark"</c>, so System was treated as Light -
    /// a light sheet and a white title bar over a page that then came out dark.
    ///
    /// Pushing: every page is kept alive, but only the visible one is in WinUI's live visual tree, and
    /// WinUI only re-themes its native controls (toggle tracks, combo boxes, text boxes, scrollbars,
    /// focus visuals - anything painted from ThemeResource) for elements in that tree. The app's own
    /// AppThemeBinding colours update everywhere; the native layer of a hidden page stays on the old
    /// theme until the page is attached, and re-themes on arrival - one frame late, the blink on tab
    /// switch. Measured: after a switch to Light on the Dashboard, the hidden Settings page's 789
    /// native elements still reported Dark. Setting RequestedTheme on a page's native root re-themes
    /// the whole subtree immediately, attached or not (also measured), so that is done for every
    /// kept-alive page under the curtain, and again whenever Windows itself flips in System mode.
    /// </summary>
    internal static class NativeTheme
    {
        /// <summary>True if <paramref name="mode"/> ("Light" / "Dark" / anything else = follow the OS)
        /// lands on a dark theme.</summary>
        public static bool ResolveIsDark(string mode) => mode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => Microsoft.Maui.Controls.Application.Current?.PlatformAppTheme == AppTheme.Dark
        };

        // Test seams for ThemeTransitionSelfCheck.
        internal static int PagesThemedForTest { get; private set; }

        /// <summary>Sets the destination theme explicitly on each page's native root. Pages without a
        /// native tree yet are skipped - they are built in the current theme on first open.</summary>
        public static void ApplyToPages(IEnumerable<Page?> pages, bool isDark)
        {
            var theme = isDark ? ElementTheme.Dark : ElementTheme.Light;
            PagesThemedForTest = 0;
            foreach (var page in pages)
            {
                if (page?.Handler?.PlatformView is FrameworkElement root)
                {
                    root.RequestedTheme = theme;
                    PagesThemedForTest++;
                }
            }
        }

        /// <summary>Native roots of the given pages that exist, with their current theme - for the
        /// self-check to prove hidden pages are already right before they are shown.</summary>
        internal static IEnumerable<(string Page, ElementTheme Actual, bool Loaded)> DescribeForTest(IEnumerable<Page?> pages)
            => pages.Where(p => p?.Handler?.PlatformView is FrameworkElement)
                    .Select(p => (p!.GetType().Name, ((FrameworkElement)p.Handler!.PlatformView!).ActualTheme, ((FrameworkElement)p.Handler.PlatformView!).IsLoaded));

        // Test seam: how many times the repaint nudge below has actually run.
        internal static int RepaintNudgesForTest { get; private set; }

        /// <summary>Every color on a kept-alive page is already correct while it is hidden - measured
        /// directly (status badges, filter chips, plain text) before, during and after a theme switch.
        /// What is NOT correct for an instant is the PIXELS: a page detached from the live window keeps
        /// its last-composited frame, and Windows does not always redraw it the moment it is reattached
        /// even though every bound value underneath has already changed - a compositor cache artifact,
        /// not a data bug. Nudging Opacity by an imperceptible amount across two frames is the standard
        /// WinUI way to force a stale Visual to recomposite; the page settles on exactly the opacity it
        /// already had, so nothing about its appearance changes except that it is now actually current.</summary>
        internal static string? LastSkipReasonForTest;

        public static void ForceRepaintOnAppear(Page page)
        {
            if (page.Handler?.PlatformView is not FrameworkElement root)
            {
                LastSkipReasonForTest = $"{page.GetType().Name}: Handler={(page.Handler == null ? "null" : "set")} PlatformView={(page.Handler?.PlatformView == null ? "null" : page.Handler.PlatformView.GetType().Name)}";
                return;
            }
            var dispatcher = page.Dispatcher;
            var was = root.Opacity;
            root.Opacity = was > 0.5 ? was - 0.001 : was + 0.001;
            RepaintNudgesForTest++;
            dispatcher?.Dispatch(() => root.Opacity = was);
        }
    }
}
#endif
