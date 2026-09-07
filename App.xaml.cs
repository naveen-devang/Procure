using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Procure.Services;

namespace Procure
{
    public partial class App : Application
    {
        private readonly IServiceProvider _services;

        public App(IServiceProvider services)
        {
            InitializeComponent();
            _services = services;

            // Diagnostic only - there was no crash trail anywhere in the app before this, so a real
            // hard crash left nothing to confirm what happened. Neither hook changes behavior: the
            // process still ends the same way, this just records why first.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Utilities.CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
                Utilities.CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);

            // OS theme flips must drop ThemeHelper's cached value. App lives for the process lifetime.
            RequestedThemeChanged += OnRequestedThemeChanged;

            // Apply persisted theme mode and pastel accent color
            var settings = _services.GetRequiredService<ISettingsService>();
            settings.ApplySavedTheme();

            // Background update check if enabled
            if (settings.AutoCheckUpdatesOnStartup)
            {
                _ = CheckForUpdatesInBackgroundAsync();
            }

            // One-time "What's New" prompt right after an update lands and relaunches the app -
            // independent of the check above, which only ever looks at *future* releases.
            _ = ShowWhatsNewIfJustUpdatedAsync();

#if DEBUG
            // Opt-in only: PROCURE_SELFCHECK=1. One environment read on a Debug launch, nothing in Release.
            if (Environment.GetEnvironmentVariable("PROCURE_SELFCHECK") == "1")
            {
                Utilities.PrLineMatcherSelfCheck.Run();
                Utilities.ClipboardItemParserSelfCheck.Run();
                Utilities.UpdateDownloadCoordinatorSelfCheck.Run();
            }

            // Opt-in only: PROCURE_UPDATE_SELFCHECK=1.
            if (Environment.GetEnvironmentVariable("PROCURE_UPDATE_SELFCHECK") == "1")
            {
                Utilities.UpdateCheckSchedulerSelfCheck.Run();
                Utilities.UpdateStateStoreSelfCheck.Run();
            }

            // The two database suites run one after the other, never at once. Both assert invariants
            // that are global - "no PR anywhere has stale search text", "no material aggregate
            // anywhere disagrees with its rows" - and a global invariant cannot hold while another
            // writer is mid-flight. Started in parallel, the flow check read the database in the
            // middle of the call-off check creating its Raw Material orders and reported 6 stale
            // aggregate rows against an app that was behaving correctly.
            _databaseSuites = RunDatabaseSuitesAsync();

            // Opt-in only: PROCURE_TODO_SELFCHECK=1.
            if (Environment.GetEnvironmentVariable("PROCURE_TODO_SELFCHECK") == "1")
            {
                _ = RunTodoSelfChecksAsync();
            }

            // Opt-in only: PROCURE_NOTE_SELFCHECK=1.
            if (Environment.GetEnvironmentVariable("PROCURE_NOTE_SELFCHECK") == "1")
            {
                _ = RunNoteSelfChecksAsync();
            }
            else
            {
                // Sweep any self-check notes a killed run left behind, so a normal Debug launch
                // never shows nfsc-/nrsc- notes in the list.
                _ = SweepSelfCheckNotesAsync();
            }

            // The same sweep for tasks. Notes had one and tasks did not, so a self-check run that was
            // killed before its cleanup left a "tfsc-..." task sitting in the real to-do list for
            // good - one from 3 September was still there when this was written.
            if (Environment.GetEnvironmentVariable("PROCURE_TODO_SELFCHECK") != "1")
            {
                _ = SweepSelfCheckTasksAsync();
            }
#endif
        }

#if DEBUG
        /// <summary>The database suites, so the board check can wait for them.</summary>
        private Task? _databaseSuites;

        private async Task RunBoardSelfCheckAfterDatabaseSuitesAsync()
        {
            if (_databaseSuites != null)
            {
                try { await _databaseSuites; } catch { /* their own logs carry their failures */ }
            }
            await Utilities.BoardMemorySelfCheck.RunAsync();
        }
#endif

#if DEBUG
        /// <summary>Runs whichever database suites were asked for, strictly in sequence.</summary>
        private async Task RunDatabaseSuitesAsync()
        {
            if (Environment.GetEnvironmentVariable("PROCURE_SELFCHECK") == "1")
            {
                await RunDatabaseSelfChecksAsync();
            }

            // Opt-in only: PROCURE_FLOW_SELFCHECK=1. The end-to-end pass over PR / RFQ / PO / merge /
            // split / shared / combined, with resource metrics. Writes to whatever database it is
            // pointed at and cleans up after itself - run it against the 20k test database.
            if (Environment.GetEnvironmentVariable("PROCURE_FLOW_SELFCHECK") == "1")
            {
                await RunProcurementFlowSelfCheckAsync();
            }
        }
#endif

#if DEBUG
        private async Task RunDatabaseSelfChecksAsync()
        {
            // Caught and written down rather than left to escape. A fire-and-forget task that throws
            // here surfaced as an unrelated "element does not have a XamlRoot" crash - the error
            // handler trying to raise a dialog before the window exists - which says nothing about
            // what actually failed and takes the process with it. An unattended run has to report.
            try
            {
                var prRepo = _services.GetRequiredService<Data.Repositories.IPurchaseRequisitionRepository>();
                await Data.DatabaseSelfCheck.RunAsync(_services.GetRequiredService<Data.SqliteDatabase>(), prRepo);
                await Data.CallOffSelfCheck.RunAsync(_services.GetRequiredService<Data.Repositories.ICallOffRepository>(), prRepo);
                Utilities.CrashLog.Write("DATABASE SELF-CHECKS PASSED");
            }
            catch (Exception ex)
            {
                Utilities.CrashLog.Write("DATABASE SELF-CHECKS FAILED", ex);
            }
        }
#endif

#if DEBUG
        private Task RunProcurementFlowSelfCheckAsync()
        {
            // Task.Run, deliberately. Started bare, this runs on the UI thread and every await inside
            // it resumes there too, so forty seconds of database work sat on the dispatcher queue -
            // long enough that the board's own delayed callbacks (card eviction, search debounce)
            // fired late and its self-check failed against a perfectly healthy app. A diagnostic that
            // freezes the thread other diagnostics are timing against is worse than no diagnostic.
            // Nothing it touches is bound to the UI, so there is nothing to marshal back.
            return Task.Run(async () =>
            {
                try
                {
                    await Data.ProcurementFlowSelfCheck.RunAsync(
                        _services.GetRequiredService<Data.SqliteDatabase>(),
                        _services.GetRequiredService<Data.Repositories.IPurchaseRequisitionRepository>());
                }
                catch (Exception ex)
                {
                    Utilities.CrashLog.Write("PROCUREMENT FLOW SELF-CHECK THREW", ex);
                }
            });
        }
#endif

#if DEBUG
        private async Task SweepSelfCheckNotesAsync()
        {
            try
            {
                var repo = _services.GetRequiredService<Data.Repositories.INoteRepository>();
                foreach (var n in (await repo.GetListAsync())
                             .Where(n => n.Title.StartsWith("nrsc-") || n.Title.StartsWith("nfsc-")))
                    await repo.DeleteAsync(n.Id);
            }
            catch { }
        }
#endif

#if DEBUG
        private async Task SweepSelfCheckTasksAsync()
        {
            try
            {
                var repo = _services.GetRequiredService<Data.Repositories.ITodoRepository>();
                foreach (var t in (await repo.GetAllAsync())
                             .Where(t => t.Title.StartsWith("tfsc-", StringComparison.Ordinal)
                                      || t.Title.StartsWith("todo-selfcheck-", StringComparison.Ordinal)))
                    await repo.DeleteAsync(t.Id);
            }
            catch { }
        }
#endif

#if DEBUG
        private async Task RunNoteSelfChecksAsync()
        {
            Data.NoteSelfCheckLog.Reset();
            var repo = _services.GetRequiredService<Data.Repositories.INoteRepository>();
            var errorHandler = _services.GetRequiredService<Services.IErrorHandler>();

            try
            {
                foreach (var n in (await repo.GetListAsync())
                             .Where(n => n.Title.StartsWith("nrsc-") || n.Title.StartsWith("nfsc-")))
                    await repo.DeleteAsync(n.Id);
            }
            catch { }

            try
            {
                await Data.NoteRepositorySelfCheck.RunAsync(repo);
                await Data.NoteFeatureSelfCheck.RunAsync(repo, errorHandler, _services.GetRequiredService<Services.ILinkTargetService>());
                await Data.LinkTargetSelfCheck.RunAsync(
                    _services.GetRequiredService<Services.ILinkTargetService>(),
                    _services.GetRequiredService<Data.Repositories.IPurchaseRequisitionRepository>());
                Data.NoteSelfCheckLog.Write("ALL NOTE SELF-CHECKS PASSED");
            }
            catch (Exception ex)
            {
                Data.NoteSelfCheckLog.Write("NOTE SELF-CHECKS FAILED: " + ex);
            }
        }
#endif

#if DEBUG
        private async Task RunTodoSelfChecksAsync()
        {
            Data.SelfCheckLog.Reset();
            var repo = _services.GetRequiredService<Data.Repositories.ITodoRepository>();
            var errorHandler = _services.GetRequiredService<Services.IErrorHandler>();

            // Sweep any self-check tasks a previous run left behind (e.g. after a crash).
            try
            {
                foreach (var t in (await repo.GetAllAsync())
                             .Where(t => t.Title.StartsWith("tfsc-") || t.Title.StartsWith("todo-selfcheck-")))
                    await repo.DeleteAsync(t.Id);
            }
            catch { }

            try
            {
                await Data.TodoRepositorySelfCheck.RunAsync(repo);
                await Data.TodoFeatureSelfCheck.RunAsync(repo, errorHandler, _services.GetRequiredService<Services.ILinkTargetService>());
                Data.SelfCheckLog.Write("ALL TODO SELF-CHECKS PASSED");
            }
            catch (Exception ex)
            {
                Data.SelfCheckLog.Write("TODO SELF-CHECKS FAILED: " + ex);
            }
            finally
            {
                // The checks wrote and removed marker tasks through their own repo; make the real
                // page model drop any it happened to cache mid-run.
                await _services.GetRequiredService<PageModels.TodoPageModel>().LoadAsync(force: true);
            }
        }
#endif

        private static void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            Procure.Utilities.ThemeHelper.Invalidate();
#if WINDOWS
            // Windows flipped (System mode) or the app did: hidden pages' native trees must follow.
            if (Shell.Current is AppShell shell) shell.ApplyNativeThemeToPages();
#endif
        }

        // No permission asked, no popup: checks every launch, then at least once every 24h for
        // as long as the app stays open. The persisted lastCheck gate (UpdateStateStore, across
        // launches) only throttles the *recurring* loop below - it used to also gate the launch
        // check itself, which meant restarting within the same 24h window (e.g. to pick up a
        // release that just went out) silently skipped checking at all. Finding an update
        // downloads it immediately in the background: the only thing the user ever sees is
        // AppShell's sidebar card, and only once the download is actually done and there's
        // something to restart into.
        private async Task CheckForUpdatesInBackgroundAsync()
        {
            await Task.Delay(3000); // Allow UI to initialize first

            var isFirstCheck = true;

            while (true)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(AppConstants.GitHubRepository))
                    {
                        var lastCheck = Utilities.UpdateStateStore.GetLastUpdateCheckUtc();
                        if (isFirstCheck || Utilities.UpdateCheckScheduler.ShouldCheckNow(lastCheck, DateTime.UtcNow))
                        {
                            var updateService = _services.GetRequiredService<IUpdateService>();
                            var update = await updateService.CheckForUpdatesAsync(AppConstants.GitHubRepository);
                            Utilities.UpdateStateStore.SetLastUpdateCheckUtc(DateTime.UtcNow);

                            if (update.IsUpdateAvailable)
                            {
                                // The coordinator inside UpdateService dedupes this against a
                                // Settings "Download & Install" click; a download already Done for
                                // this version returns immediately.
                                await updateService.DownloadUpdateAsync(update);
                                if (updateService.DownloadStatus == Utilities.UpdateDownloadStatus.Done)
                                    NotifyUpdateReady(update.TagName);
                            }
                        }
                    }
                }
                catch
                {
                    // Silently ignore - the next cycle tries again, and a failed check/download
                    // must never be visible to someone who was never asked to look at this.
                }

                isFirstCheck = false;
                await Task.Delay(Utilities.UpdateCheckScheduler.MinimumInterval);
            }
        }

        private void NotifyUpdateReady(string versionTag)
        {
            if (Current?.Windows.Count > 0 && Current.Windows[0].Page is AppShell shell)
            {
                shell.ShowUpdateReadyBanner(versionTag);
            }
        }

        private async Task ShowWhatsNewIfJustUpdatedAsync()
        {
            try
            {
                await Task.Delay(1500); // Let the window/shell finish standing up first
                var updateService = _services.GetRequiredService<IUpdateService>();
                var currentVersion = updateService.CurrentVersionString;

                var lastShown = Utilities.UpdateStateStore.GetLastWhatsNewVersionShown();
                if (string.IsNullOrEmpty(lastShown))
                {
                    // First run ever with this marker - nothing to announce, just start tracking
                    // from here so the *next* real update shows its notes.
                    Utilities.UpdateStateStore.SetLastWhatsNewVersionShown(currentVersion);
                    return;
                }

                if (lastShown == currentVersion) return;

                // Persist the marker before the network round-trip below, not after. A slow or
                // rate-limited GitHub call (the anonymous API is capped at 60 req/hour per IP) threw
                // past the old post-fetch write on some launches, so the marker never updated and the
                // dialog kept reappearing every single launch instead of once per version.
                Utilities.UpdateStateStore.SetLastWhatsNewVersionShown(currentVersion);

                var notes = string.IsNullOrWhiteSpace(AppConstants.GitHubRepository)
                    ? null
                    : await updateService.GetReleaseNotesForVersionAsync(AppConstants.GitHubRepository, currentVersion);

                var shell = await WaitForAttachedShellAsync();
                if (shell != null)
                {
                    var message = string.IsNullOrWhiteSpace(notes)
                        ? "Procure has been updated. Check Settings for release details."
                        : notes;
                    await shell.DisplayAlertAsync($"What's New in v{currentVersion}", message, "OK");
                }
            }
            catch
            {
                // Silently ignore - this is a nice-to-have, never worth blocking startup over.
            }
        }

        /// <summary>Waits until the shell has a live platform view, or gives up.
        ///
        /// A fixed 1.5s head start was not a guarantee: on a slow first launch the Shell exists as an
        /// object while its native view does not, and WinUI answers a dialog raised against it with
        /// "This element does not have a XamlRoot" - thrown on the dispatcher queue, where the calling
        /// method's own try/catch cannot see it, so it took the whole process down. Reproduced on the
        /// launch right after an update, which is the only launch this dialog runs on.</summary>
        private static async Task<Shell?> WaitForAttachedShellAsync()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (Current?.Windows.Count > 0 && Current.Windows[0].Page is Shell shell && IsAttached(shell))
                {
                    return shell;
                }
                await Task.Delay(250);
            }
            return null;
        }

        /// <summary>Ready means the native element has a XamlRoot, not merely that a handler exists.
        /// A handler is set well before the view is in the tree, and that gap is exactly where the
        /// dialog throws.</summary>
        private static bool IsAttached(Shell shell)
        {
#if WINDOWS
            return shell.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe
                   && fe.IsLoaded
                   && fe.XamlRoot != null;
#else
            return shell.Handler?.MauiContext != null;
#endif
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var shell = _services.GetRequiredService<AppShell>();
            // No explicit Width/Height: OnWindowCreated maximizes the window, and an explicit size
            // applied after that only fights the maximized state.
            var window = new Window(shell)
            {
                Title = "RWC MM Tracker",
                MinimumWidth = 800,
                MinimumHeight = 550
            };

#if DEBUG
            // Opt-in only: PROCURE_BOARD_SELFCHECK=1. Placed here rather than the constructor -
            // AppShell (just built above) is what constructs the PrListPageModel singleton this check
            // needs, and the constructor runs before AppShell exists.
            if (Environment.GetEnvironmentVariable("PROCURE_BOARD_SELFCHECK") == "1")
            {
                // After the database suites, never beside them. Half of what it checks is whether a
                // delayed callback arrives on time, and a machine busy writing to a 20,000-row
                // database makes those callbacks late - it failed on one run and passed on the next
                // with identical flags. Waiting costs nothing and removes the false alarm.
                _ = RunBoardSelfCheckAfterDatabaseSuitesAsync();
            }

            // Opt-in only: PROCURE_ACCENT_SELFCHECK=1. Cycles every accent in both modes, then restores.
            if (Environment.GetEnvironmentVariable("PROCURE_ACCENT_SELFCHECK") == "1")
            {
                _ = Utilities.AccentSelfCheck.RunAsync();
            }

            // Opt-in only: PROCURE_THEME_SELFCHECK=1. Switches the theme for real and back again.
            if (Environment.GetEnvironmentVariable("PROCURE_THEME_SELFCHECK") == "1")
            {
                _ = Utilities.ThemeTransitionSelfCheck.RunAsync();
            }

            // Opt-in only: PROCURE_PRINT_SELFCHECK=1. Measures installed drivers; prints nothing.
            if (Environment.GetEnvironmentVariable("PROCURE_PRINT_SELFCHECK") == "1")
            {
                _ = Utilities.PrintGeometrySelfCheck.RunAsync();
            }
#endif

            return window;
        }
    }
}