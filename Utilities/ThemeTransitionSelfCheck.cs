#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Procure.Data;
using Procure.PageModels;
using Procure.Services;

namespace Procure.Utilities
{
    /// <summary>
    /// The runnable check behind the theme switch: that the reveal still plays after a UI-thread
    /// stall like the one a real theme apply causes, that the curtain covers the whole window and is
    /// fully opaque when the theme is applied under it, and that one switch refreshes the sidebar
    /// highlights and repaints the Dashboard cards exactly once each.
    ///
    /// Run it by launching a Debug build with PROCURE_THEME_SELFCHECK=1 set. It switches the theme
    /// for real, twice (away and back), so the app ends where it started. Debug only, opt-in only.
    /// </summary>
    internal static class ThemeTransitionSelfCheck
    {
        public static async Task RunAsync()
        {
            var log = new StringBuilder();
            try
            {
                // Let the shell and the window settle first; the curtain needs a live XamlRoot.
                await Task.Delay(3000);
                await CheckRevealSurvivesStallAsync(log);
                await CheckRealSwitchAsync(log);
                Report("PASS", log);
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message, log);
                throw;
            }
        }

        private static Microsoft.UI.Xaml.Window NativeWindow()
            => (Microsoft.UI.Xaml.Window)Application.Current!.Windows[0].Handler!.PlatformView!;

        // --- Fix 1: a stall between apply and reveal must not turn the reveal into a cut -----------
        // The real apply stalls the UI thread for ~125ms and queues ~100ms of repaint behind it. The
        // old code waited a fixed 40ms and then queued a MAUI FadeTo, which the frame ticker handed
        // the entire stall as elapsed time: 200ms asked, 13ms measured.

        private static async Task CheckRevealSurvivesStallAsync(StringBuilder log)
        {
            var window = NativeWindow();

            // Baseline, no stall.
            await ThemeCurtain.RunAsync(window, toDark: ThemeHelper.IsDark, applyTheme: () => { });
            var baseline = ThemeCurtain.LastRevealMsForTest;
            log.AppendLine($"reveal: baseline {baseline} ms (asked {ThemeCurtain.FadeOutMs})");
            if (baseline < ThemeCurtain.FadeOutMs * 0.9)
                throw new InvalidOperationException($"Reveal took {baseline} ms with nothing in the way; asked for {ThemeCurtain.FadeOutMs}.");

            // With a 300ms UI-thread stall standing in for the theme apply, plus a repaint queued
            // behind it the way RefreshCardVisuals queues one.
            await ThemeCurtain.RunAsync(window, toDark: ThemeHelper.IsDark, applyTheme: () =>
            {
                Thread.Sleep(300);
                MainThread.BeginInvokeOnMainThread(() => Thread.Sleep(100));
            });
            var stalled = ThemeCurtain.LastRevealMsForTest;
            log.AppendLine($"reveal: after 300+100 ms stall {stalled} ms");
            if (stalled < ThemeCurtain.FadeOutMs * 0.9)
                throw new InvalidOperationException(
                    $"Reveal took {stalled} ms after a stall - the fade-out was handed the stall as elapsed time and jumped to the end. That is the hard cut.");

            // Fix 2: whole window, and opaque when the theme goes on underneath.
            var root = ThemeCurtain.LastRootSizeForTest;
            var curtain = ThemeCurtain.LastCurtainSizeForTest;
            log.AppendLine($"curtain: {curtain.Width:0}x{curtain.Height:0} over root {root.Width:0}x{root.Height:0}; opacity at apply {ThemeCurtain.OpacityAtApplyForTest}");
            if (Math.Abs(curtain.Width - root.Width) > 1 || Math.Abs(curtain.Height - root.Height) > 1)
                throw new InvalidOperationException($"Curtain is {curtain.Width:0}x{curtain.Height:0} but the window content is {root.Width:0}x{root.Height:0} - the sidebar or an edge is uncovered.");
            if (ThemeCurtain.OpacityAtApplyForTest < 0.999)
                throw new InvalidOperationException($"Theme was applied with the curtain at {ThemeCurtain.OpacityAtApplyForTest:0.00} opacity - the flip would show through.");
        }

        // --- Fixes 3 + 4: a real switch, on whatever page is up, refreshes once and repaints once ------

        private static async Task CheckRealSwitchAsync(StringBuilder log)
        {
            if (Shell.Current is not AppShell shell)
                throw new InvalidOperationException("AppShell is not the current shell.");
            var settings = IPlatformApplication.Current!.Services.GetRequiredService<ISettingsService>();
            var dashboard = IPlatformApplication.Current.Services.GetRequiredService<DashboardPageModel>();

            var original = settings.AppTheme;
            var target = original == "Dark" ? "Light" : "Dark";
            try
            {
                await CheckRealSwitchCoreAsync(shell, settings, dashboard, original, target, log);
            }
            finally
            {
                // A failing assertion must not leave the user's saved theme flipped.
                if (settings.AppTheme != original) settings.AppTheme = original;
            }
        }

        private static async Task CheckRealSwitchCoreAsync(AppShell shell, ISettingsService settings, DashboardPageModel dashboard,
                                                          string original, string target, StringBuilder log)
        {
            var highlightsBefore = shell.ThemeHighlightRefreshesForTest;
            var repaintsBefore = dashboard.CardRepaintsForTest;

            var sw = Stopwatch.StartNew();
            await shell.TransitionThemeAsync(target);
            // Let anything the apply queued at Normal priority drain before counting.
            await Task.Delay(250);

            var highlights = shell.ThemeHighlightRefreshesForTest - highlightsBefore;
            var repaints = dashboard.CardRepaintsForTest - repaintsBefore;
            log.AppendLine($"switch: {original}->{target} on {shell.CurrentPage?.GetType().Name} in {sw.ElapsedMilliseconds} ms; highlights {highlights}, dashboard repaints {repaints}; reveal {ThemeCurtain.LastRevealMsForTest} ms");

            if (settings.AppTheme != target)
                throw new InvalidOperationException($"Theme is still {settings.AppTheme} after switching to {target}.");

            // The reported bug: MAUI's title text kept the colour of the theme the app started in.
            var titleFg = TitleBarHelper.TitleForegroundForTest;
            var expectFg = target == "Dark" ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
            log.AppendLine($"title text: {titleFg} after switching to {target}");
            if (titleFg != expectFg)
                throw new InvalidOperationException($"Title text is {titleFg} in {target} mode; expected {expectFg}. 'RWC MM Tracker' would be unreadable.");
            var titleBg = TitleBarHelper.TitleBackgroundForTest;
            var expectBg = target == "Dark" ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
            if (titleBg != expectBg)
                throw new InvalidOperationException($"Title bar background is {titleBg} in {target} mode; expected the page colour {expectBg} - Mica would show through and flip on its own clock.");
            if (!TitleBarHelper.TitleBarBrushesOwnedForTest)
                throw new InvalidOperationException("MAUI replaced the title bar's background/foreground brush during the switch - Mica shows through the title bar and re-tints on its own clock: the second flip.");
            if (highlights != 1)
                throw new InvalidOperationException($"One switch refreshed the sidebar highlights {highlights} times; expected exactly 1.");
            if (repaints != 1)
                throw new InvalidOperationException($"One switch repainted the Dashboard cards {repaints} times; expected exactly 1.");
            if (ThemeCurtain.LastRevealMsForTest < ThemeCurtain.FadeOutMs * 0.9)
                throw new InvalidOperationException($"Real switch reveal took {ThemeCurtain.LastRevealMsForTest} ms - cut, not faded.");

            // And back, so the user's own choice is restored.
            await shell.TransitionThemeAsync(original);
            await Task.Delay(250);
            if (settings.AppTheme != original)
                throw new InvalidOperationException($"Could not restore the original theme {original}.");
        }

        private static void Report(string result, StringBuilder log)
        {
            Debug.WriteLine("ThemeTransitionSelfCheck: " + result);
            try
            {
                File.WriteAllText(
                    Path.Combine(DatabaseConstants.DatabaseDirectory, "theme-transition-selfcheck.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {result}{Environment.NewLine}{log}");
            }
            catch
            {
                // A diagnostic must never be the thing that breaks the run.
            }
        }
    }
}
#endif
