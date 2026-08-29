#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
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
                await CheckHiddenPagesFollowAsync(log);
                await CheckSystemModeAsync(log);
                await CheckRepaintNudgeOnAppearAsync(log);
                await CheckRevealMaskOnRaceAsync(log);
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

        // --- B1: hidden pages are already on the new theme before they are shown -------------------

        private static async Task CheckHiddenPagesFollowAsync(StringBuilder log)
        {
            var shell = (AppShell)Shell.Current!;
            var settings = IPlatformApplication.Current!.Services.GetRequiredService<ISettingsService>();
            var original = settings.AppTheme;

            // Give Settings a native tree, then leave it hidden.
            await shell.GoToAsync("//settings"); await Task.Delay(800);
            await shell.GoToAsync("//main"); await Task.Delay(500);

            var target = original == "Dark" ? "Light" : "Dark";
            try
            {
                await shell.TransitionThemeAsync(target);
                await Task.Delay(200);
                var expect = target == "Dark" ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light;
                var pages = NativeTheme.DescribeForTest(shell.KeptAlivePages).ToList();
                var hidden = pages.Where(p => !p.Loaded).ToList();
                log.AppendLine($"hidden pages after -> {target}: " + string.Join(", ", pages.Select(p => $"{p.Page}={p.Actual}{(p.Loaded ? "" : "(hidden)")}")));
                if (hidden.Count == 0)
                    throw new InvalidOperationException("No hidden page with a native tree to check - Settings should have been kept alive.");
                var stale = pages.Where(p => p.Actual != expect).ToList();
                if (stale.Count > 0)
                    throw new InvalidOperationException("Pages still on the old native theme before being shown: " +
                        string.Join(", ", stale.Select(p => p.Page)) + " - they would re-theme on arrival, the blink on tab switch.");
            }
            finally
            {
                await shell.TransitionThemeAsync(original);
                await Task.Delay(200);
            }
        }

        // --- A1 + A2: System Default resolves to the OS, and no animation is left holding ----------

        private static async Task CheckSystemModeAsync(StringBuilder log)
        {
            var shell = (AppShell)Shell.Current!;
            var settings = IPlatformApplication.Current!.Services.GetRequiredService<ISettingsService>();
            var original = settings.AppTheme;
            var osDark = Application.Current!.PlatformAppTheme == AppTheme.Dark;

            if (NativeTheme.ResolveIsDark("System") != osDark)
                throw new InvalidOperationException($"ResolveIsDark(System) = {NativeTheme.ResolveIsDark("System")} but Windows is {(osDark ? "Dark" : "Light")}.");
            if (!NativeTheme.ResolveIsDark("Dark") || NativeTheme.ResolveIsDark("Light"))
                throw new InvalidOperationException("ResolveIsDark(Dark/Light) is wrong.");

            try
            {
                // Start from the opposite of the OS so a System switch actually changes something.
                await shell.TransitionThemeAsync(osDark ? "Light" : "Dark"); await Task.Delay(200);
                await shell.TransitionThemeAsync("System"); await Task.Delay(200);

                log.AppendLine($"System switch: OS {(osDark ? "Dark" : "Light")}, sheet was {(ThemeCurtain.LastSheetIsDarkForTest ? "dark" : "light")}, title bg {TitleBarHelper.TitleBackgroundForTest}, text {TitleBarHelper.TitleForegroundForTest}");
                if (ThemeCurtain.LastSheetIsDarkForTest != osDark)
                    throw new InvalidOperationException("A switch to System Default used the wrong sheet colour - System was treated as Light/Dark by name instead of asking Windows.");

                var expectBg = osDark ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
                if (TitleBarHelper.TitleBackgroundForTest != expectBg)
                    throw new InvalidOperationException($"Title bar background is {TitleBarHelper.TitleBackgroundForTest} after System; expected {expectBg}.");

                // A2: every storyboard the switch started must have let go of its property. A held
                // one keeps showing its last frame over whatever is assigned later - the white bar.
                var held = ThemeCurtain.StoryboardsForTest.Concat(TitleBarHelper.StoryboardsForTest)
                    .Where(sb => sb.GetCurrentState() != Microsoft.UI.Xaml.Media.Animation.ClockState.Stopped).ToList();
                log.AppendLine($"storyboards after switch: {ThemeCurtain.StoryboardsForTest.Count + TitleBarHelper.StoryboardsForTest.Count}, still holding: {held.Count}");
                if (held.Count > 0)
                    throw new InvalidOperationException($"{held.Count} animation(s) are still holding their last frame after the switch - later colour assignments are stored but never shown.");
            }
            finally
            {
                await shell.TransitionThemeAsync(original);
                await Task.Delay(200);
            }
        }

        // --- The compositor-cache fix: reappearing after a switch forces one repaint nudge --------
        // Every colour on a hidden page is already correct (measured directly, both here and by hand
        // with a real build) - the flash reported was Windows reusing a detached page's last-composited
        // frame for an instant on reattach, not a stale value. NativeTheme.ForceRepaintOnAppear nudges
        // Opacity by an imperceptible amount across two frames, which is what actually clears it.

        private static async Task CheckRepaintNudgeOnAppearAsync(StringBuilder log)
        {
            var shell = (AppShell)Shell.Current!;

            // Warm each page's native tree first (a page's very first-ever reveal races its own Handler
            // creation and has no prior composited frame to correct anyway - only a REAPPEARANCE, after
            // the page has already been shown once, is the scenario the nudge exists for).
            await shell.GoToAsync("//settings"); await Task.Delay(400);
            await shell.GoToAsync("//prboard"); await Task.Delay(400);
            await shell.GoToAsync("//main"); await Task.Delay(400);

            int n0, n1, n2, n3;
            n0 = NativeTheme.RepaintNudgesForTest;
            await shell.GoToAsync("//settings"); await Task.Delay(200); n1 = NativeTheme.RepaintNudgesForTest;
            await shell.GoToAsync("//prboard"); await Task.Delay(200); n2 = NativeTheme.RepaintNudgesForTest;
            await shell.GoToAsync("//main"); await Task.Delay(200); n3 = NativeTheme.RepaintNudgesForTest;
            log.AppendLine($"repaint nudges (already-warmed pages): settings +{n1-n0}, prboard +{n2-n1}, main +{n3-n2} (total {n3-n0})");
            if (n3 - n0 < 3)
                throw new InvalidOperationException($"Only {n3-n0} of 3 REAPPEARANCES triggered a repaint nudge - a page shown a second time could show its previous frame. Last skip: {NativeTheme.LastSkipReasonForTest}");
        }

        // --- Final defence: a page shown for the first time after a theme change is masked --------
        // Repaint hints (ForceRepaintOnAppear) were not enough on their own in every report; this
        // covers it unconditionally by never letting a wrong frame be visible at all.

        private static async Task CheckRevealMaskOnRaceAsync(StringBuilder log)
        {
            var shell = (AppShell)Shell.Current!;
            var settings = IPlatformApplication.Current!.Services.GetRequiredService<ISettingsService>();
            var original = settings.AppTheme;
            var target = original == "Dark" ? "Light" : "Dark";
            try
            {
                // Warm Settings, then leave it - the switch below must mark it for masking.
                await shell.GoToAsync("//settings"); await Task.Delay(400);
                await shell.GoToAsync("//main"); await Task.Delay(200);

                await shell.TransitionThemeAsync(target);

                ThemeCurtain.LastMaskTaskForTest = null;
                await shell.GoToAsync("//settings");
                var maskTask = ThemeCurtain.LastMaskTaskForTest;
                if (maskTask == null)
                    throw new InvalidOperationException("Navigating to a page dirtied by a theme switch started no reveal mask.");
                var sw = Stopwatch.StartNew();
                await maskTask;
                log.AppendLine($"reveal mask: held+faded for {sw.ElapsedMilliseconds} ms (expect >=150ms hold + fade)");
                if (sw.ElapsedMilliseconds < 150)
                    throw new InvalidOperationException($"The reveal mask only ran {sw.ElapsedMilliseconds} ms - it did not actually hold.");
            }
            finally
            {
                await shell.TransitionThemeAsync(original);
                await Task.Delay(200);
            }
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
