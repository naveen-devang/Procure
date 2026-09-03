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
        private readonly IUpdateService _updateService;
        private readonly IServiceProvider _services;
        private bool _isManuallyToggled;
        private bool _isUpdateReady;

        public AppShell(IServiceProvider services, ISettingsService settingsService, IKeyboardShortcutService shortcuts, IUpdateService updateService)
        {
            _services = services;
            _settingsService = settingsService;
            _shortcuts = shortcuts;
            _updateService = updateService;
            Procure.Utilities.BoardTrace.Mark("shell-ctor-start");
            InitializeComponent();

            // Only the landing page is built here. Taking all four by constructor injection meant DI
            // ran every page's InitializeComponent before the first frame - Settings alone is 139
            // elements, and a session may never open it. The rest inflate in OnNavigating.
            DashboardContent.Content = services.GetRequiredService<DashboardPage>();

            UpdateThemeButtonHighlights();
            UpdateSidebarLayout(_settingsService.IsSidebarCompact);
            UpdateMaterialsTabVisibility();

            // Warm the PR Board's data while the user is still on the Dashboard - the page model holds
            // the rows and has no visual tree, so this costs a query and nothing else. Opening the board
            // otherwise pays SQLite provider start-up plus the full load on the click itself.
            // Fire and forget: PrListPageModel handles its own errors and guards re-entry.
            _ = services.GetRequiredService<PrListPageModel>().PreloadDataAsync();

            // Same treatment for the task list - one small query, no visual tree, so the tab opens
            // with data already in hand.
            _ = services.GetRequiredService<TodoPageModel>().PreloadDataAsync();

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

            if (Environment.GetEnvironmentVariable("PROCURE_OPEN_CALENDAR") == "1")
            {
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(2500), async () =>
                {
                    await GoToAsync("//todos");
                    await Task.Delay(400);
                    _services.GetRequiredService<TodoPageModel>().SetViewCommand.Execute("Calendar");
                });
            }
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

            Page? targetPage = null;
            if (PrBoardContent.Content is null && target.Contains("prboard", StringComparison.Ordinal))
            {
                Procure.Utilities.BoardTrace.Mark("click-inflate-start");
                PrBoardContent.Content = targetPage = _services.GetRequiredService<PrListPage>();
                Procure.Utilities.BoardTrace.Mark("click-inflate-done");
            }
            else if (target.Contains("prboard", StringComparison.Ordinal)) targetPage = PrBoardContent.Content as Page;
            else if (MaterialsContent.Content is null && target.Contains("materials", StringComparison.Ordinal))
                MaterialsContent.Content = targetPage = _services.GetRequiredService<CallOffPage>();
            else if (target.Contains("materials", StringComparison.Ordinal)) targetPage = MaterialsContent.Content as Page;
            else if (TasksContent.Content is null && target.Contains("todos", StringComparison.Ordinal))
                TasksContent.Content = targetPage = _services.GetRequiredService<TodoPage>();
            else if (target.Contains("todos", StringComparison.Ordinal)) targetPage = TasksContent.Content as Page;
            else if (NotesContent.Content is null && target.Contains("notes", StringComparison.Ordinal))
                NotesContent.Content = targetPage = _services.GetRequiredService<NotesPage>();
            else if (target.Contains("notes", StringComparison.Ordinal)) targetPage = NotesContent.Content as Page;
            else if (SettingsContent.Content is null && target.Contains("settings", StringComparison.Ordinal))
                SettingsContent.Content = targetPage = _services.GetRequiredService<SettingsPage>();
            else if (target.Contains("settings", StringComparison.Ordinal)) targetPage = SettingsContent.Content as Page;
            else if (target.Contains("main", StringComparison.Ordinal)) targetPage = DashboardContent.Content as Page;

#if WINDOWS
            // A page whose theme changed while it was NOT the one on screen can show a wrong frame for
            // an instant when it's shown - every colour underneath is already correct by then (measured
            // directly), so this is Windows momentarily reusing a stale composited frame, not a data
            // bug. A repaint hint at OnAppearing (see NativeTheme.ForceRepaintOnAppear) does not clear
            // it in every case that has been reported, so mask the reveal outright instead: nothing
            // wrong can be seen under an opaque cover, whatever the exact cause. Only the FIRST reveal
            // after a theme change needs it - cleared here so a normal tab switch stays instant.
            if (targetPage != null && _pagesNeedingRevealMask.Remove(targetPage)
                && Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            {
                _ = Procure.Utilities.ThemeCurtain.MaskPageRevealAsync(native, Procure.Utilities.ThemeHelper.IsDark);
            }
#endif
        }

#if WINDOWS
        private readonly HashSet<Page> _pagesNeedingRevealMask = new();
#endif

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

#if WINDOWS
            // MAUI's title text is created with the window and coloured once for the theme the app
            // started in; a saved Light theme needs it re-coloured once the tree exists.
            Dispatcher.Dispatch(Procure.Utilities.TitleBarHelper.Apply);
#endif

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

#if WINDOWS
            // ShellContent.ToolTipProperties.Text does not reach the WinUI NavigationViewItem the
            // flyout renders, so a collapsed sidebar has icons with no hover label. Set the tooltip
            // on each realised item from its own label text. Delayed: the items are not in the tree
            // yet at handler-changed. Runs again from UpdateMaterialsTabVisibility when that toggle
            // adds or removes the Raw & Packing item.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), ApplyFlyoutItemTooltips);
#endif

            FocusShellRootForKeyboard();
        }

#if WINDOWS
        private void ApplyFlyoutItemTooltips()
        {
            if (Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject root) return;

            foreach (var item in Descendants<Microsoft.UI.Xaml.Controls.NavigationViewItem>(root))
            {
                var label = Descendants<Microsoft.UI.Xaml.Controls.TextBlock>(item)
                    .Select(t => t.Text)
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                if (!string.IsNullOrWhiteSpace(label))
                    Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(item, label);
            }
        }

        private static IEnumerable<T> Descendants<T>(Microsoft.UI.Xaml.DependencyObject root)
            where T : Microsoft.UI.Xaml.DependencyObject
        {
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
#endif

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
            else if (_settingsService.IsRawPackingTabEnabled && Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.GoMaterials), e.Key)) route = "materials";
            else if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.GoTasks), e.Key)) route = "todos";
            else if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.GoNotes), e.Key)) route = "notes";
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
                case nameof(ISettingsService.IsRawPackingTabEnabled):
                    UpdateMaterialsTabVisibility();
                    break;
            }
        }

        // Hides the tab from the sidebar entirely rather than just blocking navigation to it - the
        // route still exists, but CallOffPageModel/CallOffRepository are DI singletons that never
        // get constructed until MaterialsContent.Content is actually assigned (see OnNavigating),
        // so turning this off costs nothing beyond hiding the icon: no query, no PoChangeNotifier
        // subscription, nothing sitting in memory.
        private void UpdateMaterialsTabVisibility()
        {
            var enabled = _settingsService.IsRawPackingTabEnabled;
            Shell.SetFlyoutItemIsVisible(MaterialsContent, enabled);

            // Being turned off while it's the page on screen would otherwise strand the user on a
            // tab whose sidebar entry just disappeared.
            if (!enabled && CurrentPage is Pages.CallOffPage)
            {
                _ = GoToAsync("//main");
            }

#if WINDOWS
            // Re-tooltip: turning the tab on realises a new NavigationViewItem with no tooltip yet.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), ApplyFlyoutItemTooltips);
#endif
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

            UpdateUpdateBannerVisibility();
        }

        // Expanded sidebar gets the full card; compact (icon-only) has no room for it, so a small
        // dot stands in instead - both driven off the same _isUpdateReady flag, split only by
        // which layout is currently showing.
        private void UpdateUpdateBannerVisibility()
        {
            if (UpdateReadyCard != null)
                UpdateReadyCard.IsVisible = _isUpdateReady && !_settingsService.IsSidebarCompact;

            if (CompactUpdateDot != null)
                CompactUpdateDot.IsVisible = _isUpdateReady && _settingsService.IsSidebarCompact;
        }

        // Called from App.xaml.cs once a silently-downloaded update is actually ready to apply -
        // never in response to anything the user asked for, so this only ever turns the card on,
        // it doesn't interrupt whatever the user is doing.
        public void ShowUpdateReadyBanner(string versionTag)
        {
            _isUpdateReady = true;
            if (UpdateReadyTitleLabel != null)
                UpdateReadyTitleLabel.Text = string.IsNullOrWhiteSpace(versionTag) ? "Update ready" : $"Update ready — {versionTag}";
            UpdateUpdateBannerVisibility();
        }

        public void HideUpdateReadyBanner()
        {
            _isUpdateReady = false;
            UpdateUpdateBannerVisibility();
        }

        private void OnDismissUpdateBannerClicked(object? sender, EventArgs e) => HideUpdateReadyBanner();

        // The update was already downloaded silently in the background - this just applies it.
        // Velopack exits the process itself on success, so there is normally nothing after this
        // call; LaunchInstaller's bool return only matters on the rare failure path.
        private void OnRestartForUpdateClicked(object? sender, EventArgs e)
        {
            if (!_updateService.LaunchInstaller(string.Empty))
            {
                HideUpdateReadyBanner();
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

        // Internal so the Settings page's theme picker goes through the same curtain instead of
        // hard-flipping the whole app. Setting AppTheme raises SettingsChanged, which is what refreshes
        // the button highlights and the title bar - once. They used to be called explicitly here as
        // well, so every switch ran them twice and started two competing fades on the same buttons.
        internal async Task TransitionThemeAsync(string targetTheme, Button? clickedBtn = null)
        {
            if (_isThemeTransitioning || _settingsService.AppTheme == targetTheme) return;
            _isThemeTransitioning = true;

            try
            {
                if (clickedBtn != null)
                {
                    _ = clickedBtn.ScaleToAsync(0.85, 70, Easing.CubicOut)
                        .ContinueWith(_ => MainThread.BeginInvokeOnMainThread(async () => await clickedBtn.ScaleToAsync(1.0, 120, Easing.CubicIn)));
                }

                if (CompactThemeBtn != null)
                {
                    _ = CompactThemeBtn.RelRotateToAsync(180, 250, Easing.CubicOut);
                }

#if WINDOWS
                // One curtain over the whole window - sidebar, page, any open modal - rather than one
                // per page. See ThemeCurtain for why it has to be a compositor animation.
                if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
                {
                    // "System" resolves to whatever Windows is; deciding with target == "Dark" sent a
                    // light sheet and a white title bar over a page that then came out dark.
                    var isDark = Procure.Utilities.NativeTheme.ResolveIsDark(targetTheme);
                    await Procure.Utilities.ThemeCurtain.RunAsync(native, isDark, () =>
                    {
                        _settingsService.AppTheme = targetTheme;
                        ApplyNativeThemeToPages();
                    });
                    return;
                }
#endif
                _settingsService.AppTheme = targetTheme;
            }
            catch
            {
                _settingsService.AppTheme = targetTheme;
            }
            finally
            {
                _isThemeTransitioning = false;
            }
        }

#if WINDOWS
        /// <summary>Every page this shell keeps alive - null until first opened.</summary>
        internal IEnumerable<Page?> KeptAlivePages => new Page?[]
        {
            DashboardContent.Content as Page, PrBoardContent.Content as Page, MaterialsContent.Content as Page,
            TasksContent.Content as Page, NotesContent.Content as Page, SettingsContent.Content as Page
        };

        /// <summary>Pushes the current theme into every kept-alive page's native tree - the hidden ones
        /// would otherwise keep the old native theme until shown. Called under the curtain on a switch,
        /// and from App when Windows flips theme while the app follows the OS.</summary>
        internal void ApplyNativeThemeToPages()
        {
            Procure.Utilities.NativeTheme.ApplyToPages(KeptAlivePages, Procure.Utilities.ThemeHelper.IsDark);

            // Every page but the current one is about to be shown, at some later point, in a theme it
            // has never been rendered in. OnNavigating masks exactly that first reveal (see there).
            foreach (var page in KeptAlivePages)
            {
                if (page != null && !ReferenceEquals(page, CurrentPage)) _pagesNeedingRevealMask.Add(page);
            }
        }
#endif

        // Test seam for ThemeTransitionSelfCheck: one switch must land here exactly once.
        internal int ThemeHighlightRefreshesForTest { get; private set; }

        private void UpdateThemeButtonHighlights()
        {
            ThemeHighlightRefreshesForTest++;
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
