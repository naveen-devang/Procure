#if WINDOWS
using Microsoft.UI.Windowing;
using Windows.UI;

namespace Procure.Utilities
{
    /// <summary>
    /// The native window's title bar (caption text, min/max/close buttons) is OS chrome, not part of
    /// the MAUI page tree - AppThemeBinding, UserAppTheme, none of it reaches it. Left alone, it always
    /// shows whatever color Windows itself picked (its own dark/light setting, or the "show accent
    /// color on title bars" personalization option), regardless of which theme the user picked inside
    /// this app - which is exactly the mismatch in the screenshot: a light-themed app under a
    /// system-tinted title bar. This is the one place that has to reach past MAUI into
    /// Microsoft.UI.Windowing.AppWindow.TitleBar to keep the two in sync.
    /// </summary>
    internal static class TitleBarHelper
    {
        private static AppWindow? _appWindow;

        /// <summary>Captures the AppWindow once, at window creation, and applies the current theme.</summary>
        public static void Initialize(AppWindow appWindow)
        {
            _appWindow = appWindow;
            Apply();
        }

        /// <summary>Re-applies the title bar colors for whatever ThemeHelper.IsDark resolves to right
        /// now. Call this from every place the app's own theme choice can change - the same set of
        /// hooks AppShell already uses to refresh its own theme-dependent UI.</summary>
        public static void Apply()
        {
            var titleBar = _appWindow?.TitleBar;
            if (titleBar is null) return;

            var isDark = ThemeHelper.IsDark;
            var background = isDark ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
            var foreground = isDark ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
            var hoverBg = isDark ? Windows.UI.Color.FromArgb(255, 0x3A, 0x3A, 0x3A) : Windows.UI.Color.FromArgb(255, 0xE5, 0xE5, 0xE5);
            var pressedBg = isDark ? Windows.UI.Color.FromArgb(255, 0x4A, 0x4A, 0x4A) : Windows.UI.Color.FromArgb(255, 0xD5, 0xD5, 0xD5);

            titleBar.BackgroundColor = background;
            titleBar.InactiveBackgroundColor = background;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveForegroundColor = foreground;

            titleBar.ButtonBackgroundColor = background;
            titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hoverBg;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressedBg;
            titleBar.ButtonPressedForegroundColor = foreground;
        }
    }
}
#endif
