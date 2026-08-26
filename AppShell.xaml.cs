using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Procure.PageModels;
using Procure.Pages;
using Procure.Services;

namespace Procure
{
    public partial class AppShell : Shell
    {
        private readonly ISettingsService _settingsService;
        private readonly IKeyboardShortcutService _shortcuts;
        private readonly IServiceProvider _services;
        private bool _isManuallyToggled;

        public AppShell(IServiceProvider services, ISettingsService settingsService, IKeyboardShortcutService shortcuts)
        {
            _services = services;
            _settingsService = settingsService;
            _shortcuts = shortcuts;
            Procure.Utilities.BoardTrace.Mark("shell-ctor-start");
            InitializeComponent();

            // Only the landing page is built here. Taking all four by constructor injection meant DI
            // ran every page's InitializeComponent before the first frame - Settings alone is 139
            // elements, and a session may never open it. The rest inflate in OnNavigating.
            DashboardContent.Content = services.GetRequiredService<DashboardPage>();

            UpdateThemeButtonHighlights();
            UpdateSidebarLayout(_settingsService.IsSidebarCompact);

            // Warm the PR Board's data while the user is still on the Dashboard - the page model holds
            // the rows and has no visual tree, so this costs a query and nothing else. Opening the board
            // otherwise pays SQLite provider start-up plus the full load on the click itself.
            // Fire and forget: PrListPageModel handles its own errors and guards re-entry.
            _ = services.GetRequiredService<PrListPageModel>().PreloadDataAsync();

            // Warm the board's visual tree too. 1.2s clears the startup busy stretch (~1.1s measured
            // on the trace heartbeat) so the prewarm never delays first paint, while still beating any
            // humanly-plausible click; OnNavigating's null check makes it race-free if one lands first.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(1200), () =>
            {
                if (PrBoardContent.Content is null)
                {
                    Procure.Utilities.BoardTrace.Mark("prewarm-inflate-start");
                    PrBoardContent.Content = _services.GetRequiredService<PrListPage>();
                    Procure.Utilities.BoardTrace.Mark("prewarm-inflate-done");
                }
            });

            Procure.Utilities.BoardTrace.Mark("shell-ctor-done");
            Procure.Utilities.BoardTrace.StartPulse(Dispatcher);
            if (Procure.Utilities.BoardTrace.IsEnabled
                && int.TryParse(Environment.GetEnvironmentVariable("PROCURE_TRACE_NAV"), out var navMs))
            {
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(navMs), async () =>
                {
                    Procure.Utilities.BoardTrace.Mark("nav-click");
                    await GoToAsync("//prboard");
                    Procure.Utilities.BoardTrace.Mark("nav-completed");
                });
            }
        }

        // Pages are DI singletons assigned to their ShellContent once, so this is purely about *when*
        // the assignment happens - nothing is ever re-inflated on a later tab switch.
        protected override void OnNavigating(ShellNavigatingEventArgs args)
        {
            base.OnNavigating(args);

            var target = args.Target?.Location?.OriginalString;
            if (string.IsNullOrEmpty(target)) return;

            if (PrBoardContent.Content is null && target.Contains("prboard", StringComparison.Ordinal))
            {
                Procure.Utilities.BoardTrace.Mark("click-inflate-start");
                PrBoardContent.Content = _services.GetRequiredService<PrListPage>();
                Procure.Utilities.BoardTrace.Mark("click-inflate-done");
            }
            else if (ColumnsContent.Content is null && target.Contains("columns", StringComparison.Ordinal))
                ColumnsContent.Content = _services.GetRequiredService<ManageColumnsPage>();
            else if (MaterialsContent.Content is null && target.Contains("materials", StringComparison.Ordinal))
                MaterialsContent.Content = _services.GetRequiredService<CallOffPage>();
            else if (SettingsContent.Content is null && target.Contains("settings", StringComparison.Ordinal))
                SettingsContent.Content = _services.GetRequiredService<SettingsPage>();
        }

        // Subscribe/unsubscribe in matching pairs, tied to the handler lifetime. Subscribing in the
        // constructor instead would leave these dead after any disconnect/reconnect cycle, silently
        // killing sidebar auto-collapse and the theme-button highlight.
        protected override void OnHandlerChanged()
        {
            base.OnHandlerChanged();

            _settingsService.SettingsChanged -= OnSettingsChanged;
            _settingsService.SettingsChanged += OnSettingsChanged;
            SizeChanged -= OnShellSizeChanged;
            SizeChanged += OnShellSizeChanged;

            // App-wide shortcuts (page switching, sidebar toggle, shortcut recording) live here
            // rather than on any one page's hook, since Shell's root is the only element alive
            // for the whole session regardless of which of the four pages is showing. PreviewKeyDown
            // tunnels root-to-focused-element, so this fires before PrListPage's own page-level hook
            // gets a turn - as long as this only marks e.Handled for the keys it actually recognizes,
            // every page-level and per-control shortcut further down the tree keeps working unchanged.
            if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement root)
            {
                root.PreviewKeyDown -= OnShellPreviewKeyDown;
                root.PreviewKeyDown += OnShellPreviewKeyDown;
            }

            FocusShellRootForKeyboard();
        }

        // WinUI only routes PreviewKeyDown along the path to whatever element currently holds logical
        // keyboard focus - with nothing focused anywhere (true right after launch, and true on
        // Dashboard/Columns/Settings, which have no page-level focus claim of their own), no key
        // reaches this hook at all until the user clicks or arrow-keys something into focus first.
        // Grabbing programmatic focus here closes that gap without needing a visible focus target.
        private void FocusShellRootForKeyboard()
        {
            if (Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement root) return;
            root.IsTabStop = true;
            root.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }

        // Re-claimed on every page switch: ShellContent's own content-presenter swap can drop focus
        // entirely, which would otherwise silently kill every global shortcut until the next click.
        protected override void OnNavigated(ShellNavigatedEventArgs args)
        {
            base.OnNavigated(args);
            FocusShellRootForKeyboard();
        }

        protected override void OnHandlerChanging(HandlerChangingEventArgs args)
        {
            base.OnHandlerChanging(args);

            if (args.NewHandler is null)
            {
                _settingsService.SettingsChanged -= OnSettingsChanged;
                SizeChanged -= OnShellSizeChanged;
                if (args.OldHandler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement root)
                {
                    root.PreviewKeyDown -= OnShellPreviewKeyDown;
                }
            }
        }

        private void OnShellPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // The Settings page's shortcut recorder takes priority over everything else: while it's
            // armed, the very next real key becomes the new binding instead of triggering whatever
            // action it happened to already be bound to.
            var recordingId = _shortcuts.RecordingActionId;
            if (recordingId != null)
            {
                if (Procure.Utilities.ShortcutInput.IsModifierKey(e.Key)) return; // wait for the real key
                e.Handled = true;

                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    _shortcuts.RecordingActionId = null; // cancelled - binding left unchanged
                    return;
                }

                var combo = Procure.Utilities.ShortcutInput.Capture(e.Key);
                var conflict = _shortcuts.FindConflict(combo, recordingId);
                _shortcuts.RecordingActionId = null;

                if (conflict != null)
                {
                    var conflictName = Procure.Utilities.KeyboardShortcutRegistry.Get(conflict).DisplayName;
                    _ = DisplayAlertAsync("Shortcut Already In Use",
                        $"{combo} is already assigned to \"{conflictName}\". Choose a different combination.", "OK");
                    return;
                }

                _shortcuts.SetCombo(recordingId, combo);
                return;
            }

            string? route = null;
            if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.GoDashboard), e.Key)) route = "main";
            else if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.GoPrBoard), e.Key)) route = "prboard";
            else if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.GoColumns), e.Key)) route = "columns";
            else if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.GoSettings), e.Key)) route = "settings";

            if (route != null)
            {
                e.Handled = true;
                // An open PR Board modal takes priority over navigating away from under it - same
                // rule Escape already follows via CloseTopmostModal.
                if (PageModels.PrListPageModel.Current?.CloseTopmostModal() == true) return;
                _ = GoToAsync($"//{route}");
                return;
            }

            if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.ToggleSidebar), e.Key))
            {
                e.Handled = true;
                OnToggleSidebarClicked(null, EventArgs.Empty);
            }
        }

        private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
        {
            switch (e.Key)
            {
                case nameof(ISettingsService.AppTheme):
                case nameof(ISettingsService.AccentTheme):
                    UpdateThemeButtonHighlights();
                    break;
                case nameof(ISettingsService.IsSidebarCompact):
                    UpdateSidebarLayout(_settingsService.IsSidebarCompact);
                    break;
            }
        }

        private void OnShellSizeChanged(object? sender, EventArgs e)
        {
            if (!_settingsService.AutoCollapseSidebarOnNarrow || _isManuallyToggled)
                return;

            if (Width > 0)
            {
                if (Width < AppConstants.ResponsiveCollapseBreakpoint && !_settingsService.IsSidebarCompact)
                {
                    _settingsService.IsSidebarCompact = true;
                }
                else if (Width >= AppConstants.ResponsiveCollapseBreakpoint && _settingsService.IsSidebarCompact)
                {
                    _settingsService.IsSidebarCompact = false;
                }
            }
        }

        private void OnToggleSidebarClicked(object? sender, EventArgs e)
        {
            _isManuallyToggled = true;
            _settingsService.IsSidebarCompact = !_settingsService.IsSidebarCompact;
        }

        private void UpdateSidebarLayout(bool isCompact)
        {
            FlyoutWidth = isCompact ? AppConstants.SidebarCompactWidth : AppConstants.SidebarExpandedWidth;

            if (FlyoutHeaderGrid != null)
            {
                FlyoutHeaderGrid.Padding = isCompact ? new Thickness(14, 14, 14, 8) : new Thickness(14, 14, 14, 8);
            }

            if (SidebarToggleBtn != null)
            {
                ToolTipProperties.SetText(SidebarToggleBtn, isCompact ? "Expand navigation (Ctrl+B)" : "Collapse navigation (Ctrl+B)");
            }

            if (ExpandedFooterLayout != null)
            {
                ExpandedFooterLayout.IsVisible = !isCompact;
            }

            if (CompactFooterLayout != null)
            {
                CompactFooterLayout.IsVisible = isCompact;
            }
        }

        private bool _isThemeTransitioning;

        private async void OnLightModeClicked(object? sender, EventArgs e)
        {
            await TransitionThemeAsync("Light", LightModeBtn);
        }

        private async void OnDarkModeClicked(object? sender, EventArgs e)
        {
            await TransitionThemeAsync("Dark", DarkModeBtn);
        }

        private async void OnCompactThemeClicked(object? sender, EventArgs e)
        {
            var isDark = Procure.Utilities.ThemeHelper.IsDark;
            await TransitionThemeAsync(isDark ? "Light" : "Dark", CompactThemeBtn);
        }

        // Internal so the Settings page's theme picker goes through the same curtain/fade instead of
        // hard-flipping the whole app.
        internal async Task TransitionThemeAsync(string targetTheme, Button? clickedBtn = null)
        {
            if (_isThemeTransitioning) return;
            _isThemeTransitioning = true;

            var isGoingToDark = targetTheme == "Dark";

            try
            {
                // 1. Button micro-animations
                if (clickedBtn != null)
                {
                    _ = clickedBtn.ScaleToAsync(0.85, 70, Easing.CubicOut)
                        .ContinueWith(_ => MainThread.BeginInvokeOnMainThread(async () => await clickedBtn.ScaleToAsync(1.0, 120, Easing.CubicIn)));
                }

                if (CompactThemeBtn != null)
                {
                    _ = CompactThemeBtn.RelRotateToAsync(180, 250, Easing.CubicOut);
                }

                // 2. Execute GPU-accelerated curtain transition on current page if supported
                if (CurrentPage is IThemeTransitionable transitionablePage)
                {
                    await transitionablePage.AnimateThemeTransitionAsync(() =>
                    {
                        _settingsService.AppTheme = targetTheme;
                        UpdateThemeButtonHighlights();
                    }, isGoingToDark);
                }
                else if (CurrentPage != null)
                {
                    await CurrentPage.FadeToAsync(0.5, 140, Easing.CubicOut);
                    _settingsService.AppTheme = targetTheme;
                    UpdateThemeButtonHighlights();
                    await Task.Delay(35);
                    await CurrentPage.FadeToAsync(1.0, 180, Easing.CubicIn);
                }
                else
                {
                    _settingsService.AppTheme = targetTheme;
                    UpdateThemeButtonHighlights();
                }
            }
            catch
            {
                _settingsService.AppTheme = targetTheme;
                UpdateThemeButtonHighlights();
            }
            finally
            {
                _isThemeTransitioning = false;
            }
        }

        private void UpdateThemeButtonHighlights()
        {
            var isDark = Procure.Utilities.ThemeHelper.IsDark;
            if (LightModeBtn != null && DarkModeBtn != null)
            {
                _ = LightModeBtn.FadeToAsync(isDark ? 0.4 : 1.0, 150, Easing.CubicInOut);
                _ = DarkModeBtn.FadeToAsync(isDark ? 1.0 : 0.4, 150, Easing.CubicInOut);
            }

            if (CompactThemeBtn != null)
            {
                CompactThemeBtn.Text = isDark ? "\uE708" : "\uE706";
                ToolTipProperties.SetText(CompactThemeBtn, isDark ? "Switch to Light Theme" : "Switch to Dark Theme");
            }

#if WINDOWS
            // The native title bar is OS chrome, outside the MAUI page tree AppThemeBinding reaches -
            // every path that lands here is also a path where the app's own theme choice changed.
            Procure.Utilities.TitleBarHelper.Apply();
#endif
        }
    }
}
