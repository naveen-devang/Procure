#if WINDOWS
using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Color = Windows.UI.Color;

namespace Procure.Utilities
{
    /// <summary>
    /// Keeps the window's title bar in step with the app's own theme choice. Two different things
    /// live up there and neither follows AppThemeBinding:
    ///
    /// The caption buttons (min/max/close) are OS chrome, coloured only through
    /// AppWindow.TitleBar.Button* - and since MAUI extends content into the title bar, those Button*
    /// colours are the ONLY AppWindow.TitleBar colours that do anything; Foreground/BackgroundColor
    /// are ignored.
    ///
    /// The title text ("RWC MM Tracker") is MAUI's own XAML TextBlock, named AppTitle. MAUI writes its
    /// Foreground once from Shell.TitleColor at window creation and never again: Shell.TitleColor does
    /// re-evaluate on a theme change (measured), but with NavBarIsVisible=False there is no toolbar to
    /// carry it to the TextBlock, and even Shell.SetTitleColor(Red) leaves the text untouched. So an
    /// app that starts Dark keeps white title text in Light mode forever - the reported bug. This
    /// helper takes over that TextBlock's brush directly.
    ///
    /// The title bar's own background is a third thing: MAUI leaves it transparent, so the window's
    /// Mica backdrop shows through - and Mica re-tints itself on a theme change on its own schedule
    /// (measured: the band under the curtain went B5 -> A1 -> D7 -> F3 over ~200ms while the page
    /// beneath was already F3), visibly out of step with the curtain: a second flip after the reveal.
    /// So the window's root grid gets a solid brush in the page colour, animated in lockstep, and
    /// Mica never shows anywhere. (Not the title bar container: it is a ContentControl whose template
    /// does not paint Background - the brush sat there, measured as attached, and painted nothing.)
    /// Every page already paints an opaque page-colour background, so this changes nothing below
    /// the title bar.
    ///
    /// Neither the text nor the caption glyphs can be covered by ThemeCurtain, so during a switch
    /// they are animated in lockstep with it: they dissolve into the incoming sheet colour as the
    /// curtain fades in, and re-emerge in their new colour as it fades out. Text and background use a
    /// compositor ColorAnimation like the curtain itself. The caption buttons' background is kept
    /// transparent - drawn opaque by the OS it sat on top of the curtain as a solid block - so only
    /// their glyph colour is OS-owned, and that is stepped coarsely.
    /// </summary>
    internal static class TitleBarHelper
    {
        private sealed record Palette(Color Bg, Color Fg, Color Hover, Color Pressed);

        private static readonly Palette DarkPalette = new(
            Color.FromArgb(255, 0x20, 0x20, 0x20), Color.FromArgb(255, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(255, 0x3A, 0x3A, 0x3A), Color.FromArgb(255, 0x4A, 0x4A, 0x4A));

        private static readonly Palette LightPalette = new(
            Color.FromArgb(255, 0xF3, 0xF3, 0xF3), Color.FromArgb(255, 0x1A, 0x1A, 0x1A),
            Color.FromArgb(255, 0xE5, 0xE5, 0xE5), Color.FromArgb(255, 0xD5, 0xD5, 0xD5));

        private static AppWindow? _appWindow;
        private static TextBlock? _title;
        private static Panel? _rootGrid;
        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TitleBrush = new(DarkPalette.Fg);
        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TitleBgBrush = new(DarkPalette.Bg);
        private static bool _animating;
        private static bool _retryArmed;

        private static void OnRootLayoutUpdated(object? sender, object e)
        {
            if (TryAttachTitle()) Apply();
        }

        /// <summary>Test seams: the title text's and title bar background's current colours.</summary>
        internal static Color TitleForegroundForTest => TitleBrush.Color;
        internal static Color TitleBackgroundForTest => TitleBgBrush.Color;
        internal static readonly System.Collections.Generic.List<Storyboard> StoryboardsForTest = new();
        internal static bool TitleBarBrushesOwnedForTest =>
            _rootGrid != null && ReferenceEquals(_rootGrid.Background, TitleBgBrush)
            && _title != null && ReferenceEquals(_title.Foreground, TitleBrush);

        /// <summary>Captures the AppWindow once, at window creation, and applies the current theme.</summary>
        public static void Initialize(AppWindow appWindow)
        {
            _appWindow = appWindow;
            Apply();
        }

        /// <summary>Re-applies the title bar for whatever ThemeHelper.IsDark resolves to right now.
        /// Called from every place the app's own theme choice can change; a no-op mid-transition,
        /// where the animation below is already carrying the colours to the same end state.</summary>
        public static void Apply()
        {
            if (_animating) return;
            var p = ThemeHelper.IsDark ? DarkPalette : LightPalette;
            SetButtons(p.Fg, p.Hover, p.Pressed);
            if (TryAttachTitle())
            {
                TitleBrush.Color = p.Fg;
                TitleBgBrush.Color = p.Bg;
            }
        }

        /// <summary>Curtain fade-in: title text and caption glyphs sink into the incoming sheet colour,
        /// so by the time the curtain is opaque the whole window - title bar included - is one flat
        /// colour. Runs concurrently with the curtain's own fade.</summary>
        public static Task FadeIntoSheetAsync(bool toDark, int ms)
        {
            var from = ThemeHelper.IsDark ? DarkPalette : LightPalette;
            var to = toDark ? DarkPalette : LightPalette;
            _animating = true;
            TryAttachTitle();
            StoryboardsForTest.Clear();
            return Task.WhenAll(
                AnimateAsync(TitleBrush, to.Bg, ms),
                AnimateAsync(TitleBgBrush, to.Bg, ms),
                StepGlyphsAsync(from.Fg, to.Bg, ms));
        }

        /// <summary>Curtain fade-out: text and glyphs re-emerge from the sheet colour in their new
        /// colour, then everything lands on the exact final palette.</summary>
        public static async Task EmergeFromSheetAsync(bool toDark, int ms)
        {
            var to = toDark ? DarkPalette : LightPalette;
            try
            {
                await Task.WhenAll(
                    AnimateAsync(TitleBrush, to.Fg, ms),
                    StepGlyphsAsync(to.Bg, to.Fg, ms));
            }
            finally
            {
                _animating = false;
                Apply();
            }
        }

        /// <summary>Recovers from a transition that threw before EmergeFromSheetAsync ran.</summary>
        public static void EndTransition()
        {
            _animating = false;
            Apply();
        }

        private static bool TryAttachTitle()
        {
            if (_title != null) return true;
            if (Microsoft.Maui.Controls.Application.Current?.Windows.Count is not > 0) return false;
            if (Microsoft.Maui.Controls.Application.Current.Windows[0].Handler?.PlatformView is not Microsoft.UI.Xaml.Window window) return false;
            if (window.Content is not FrameworkElement root) return false;

            _title = Find<TextBlock>(root, "AppTitle");
            _rootGrid = Find<Panel>(root, "RootGrid");
            if (_title == null || _rootGrid == null)
            {
                // MAUI builds the title bar from a template after the window content is set, so the
                // first Apply (a saved Light theme, cold start) can land before the element exists.
                // Try again on the next layout pass, until it does.
                if (!_retryArmed)
                {
                    _retryArmed = true;
                    root.LayoutUpdated += OnRootLayoutUpdated;
                }
                return false;
            }
            if (_retryArmed) { root.LayoutUpdated -= OnRootLayoutUpdated; _retryArmed = false; }
            _title.Foreground = TitleBrush;
            _rootGrid.Background = TitleBgBrush;
            return true;

            static T? Find<T>(DependencyObject node, string name) where T : FrameworkElement
            {
                if (node is T fe && fe.Name == name) return fe;
                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    var hit = Find<T>(VisualTreeHelper.GetChild(node, i), name);
                    if (hit != null) return hit;
                }
                return null;
            }
        }

        private static Task AnimateAsync(Microsoft.UI.Xaml.Media.SolidColorBrush brush, Color to, int ms)
        {
            if (_title == null) return Task.CompletedTask;
            var tcs = new TaskCompletionSource();
            var anim = new ColorAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, brush);
            Storyboard.SetTargetProperty(anim, "Color");
            var storyboard = new Storyboard();
            storyboard.Children.Add(anim);
            // Commit, then Stop: a held (HoldEnd) animation would keep showing its last frame over
            // every later assignment - which is exactly how the title bar stayed white after a switch
            // to System Default resolved to Dark. See ThemeCurtain.FadeAsync.
            storyboard.Completed += (_, _) => { brush.Color = to; storyboard.Stop(); tcs.TrySetResult(); };
            StoryboardsForTest.Add(storyboard);
            storyboard.Begin();
            return tcs.Task;
        }

        // Every AppWindowTitleBar property set costs ~1.5ms of UI thread (it redraws the caption), so
        // this is deliberately coarse: four properties, ~40ms apart. On a 10px caption glyph that is
        // indistinguishable from a per-frame fade; the full-frame version measured a 609ms reveal.
        private static async Task StepGlyphsAsync(Color from, Color to, int ms)
        {
            var titleBar = _appWindow?.TitleBar;
            if (titleBar is null) return;
            const int frame = 30; // Task.Delay rounds up to the 15.6ms timer tick: ~31-47ms real
            var steps = Math.Max(2, ms / frame);
            for (var i = 1; i <= steps; i++)
            {
                var t = (float)i / steps;
                t = (float)(0.5 - 0.5 * Math.Cos(t * Math.PI)); // same SineInOut as the curtain
                var fg = Mix(from, to, t);
                titleBar.ButtonForegroundColor = fg;
                titleBar.ButtonInactiveForegroundColor = fg;
                if (i < steps) await Task.Delay(frame);
            }
        }

        private static Color Mix(Color a, Color b, float t) => Color.FromArgb(255,
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        private static void SetButtons(Color foreground, Color hover, Color pressed)
        {
            var titleBar = _appWindow?.TitleBar;
            if (titleBar is null) return;

            // Transparent: the buttons then sit on the title bar's own (curtain-covered) background
            // instead of an OS-drawn opaque block above it. Hover/pressed stay opaque - they only
            // exist while the pointer is on a button, never during a switch.
            var transparent = Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonBackgroundColor = transparent;
            titleBar.ButtonInactiveBackgroundColor = transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonPressedForegroundColor = foreground;
        }
    }
}
#endif
