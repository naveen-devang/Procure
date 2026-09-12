using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Procure.Data.Repositories;
using Procure.Utilities;

namespace Procure.Data
{
    /// <summary>
    /// Every self-check the app can run, and the order they have to run in - in Procure.Core, so any
    /// head can run them.
    ///
    /// This orchestration used to live in the MAUI App.xaml.cs, which meant the whole suite - the
    /// 183-check procurement flow, the database invariants, todo, note, clipboard, call-off, update
    /// coordinator - could only be run by the binary being retired. The WinUI app, the one colleagues
    /// will actually run, had no way to check itself at all.
    ///
    /// Every suite is opt-in through an environment variable, so a normal launch reads a few strings
    /// and does nothing else.
    /// </summary>
    public static class SelfCheckSuite
    {
        private static bool On(string name) => Environment.GetEnvironmentVariable(name) == "1";

        /// <summary>Starts whatever was asked for and returns a task that completes when the database
        /// suites do. The caller need not await it - a head that wants to show its window first can
        /// drop it.</summary>
        public static Task RunAsync(IServiceProvider services)
        {
            // One phase of the flow check needs the board page model; the rest is repository work.
            ProcurementFlowSelfCheck.HostServices = services;

            if (On("PROCURE_SELFCHECK"))
            {
                PrLineMatcherSelfCheck.Run();
                ClipboardItemParserSelfCheck.Run();
                UpdateDownloadCoordinatorSelfCheck.Run();
            }

            if (On("PROCURE_UPDATE_SELFCHECK"))
            {
                UpdateCheckSchedulerSelfCheck.Run();
                UpdateStateStoreSelfCheck.Run();
            }

            // Drives the live board, so it is never mixed with anything else.
            if (On("PROCURE_BOARD_MEMORY")) return BoardRetentionProbe.RunAsync(services);

            var databaseSuites = RunDatabaseSuitesAsync(services);

            if (On("PROCURE_TODO_SELFCHECK")) _ = RunTodoSelfChecksAsync(services);
            else _ = SweepAsync(services, todo: true);

            if (On("PROCURE_NOTE_SELFCHECK")) _ = RunNoteSelfChecksAsync(services);
            else _ = SweepAsync(services, todo: false);

            return databaseSuites;
        }

        /// <summary>The two database suites run one after the other, never at once. Both assert
        /// invariants that are global - "no PR anywhere has stale search text", "no material aggregate
        /// anywhere disagrees with its rows" - and a global invariant cannot hold while another writer
        /// is mid-flight. Started in parallel, the flow check read the database in the middle of the
        /// call-off check creating its Raw Material orders and reported 6 stale aggregate rows against
        /// an app that was behaving correctly.</summary>
        private static async Task RunDatabaseSuitesAsync(IServiceProvider services)
        {
            if (On("PROCURE_SELFCHECK"))
            {
                // Caught and written down rather than left to escape: a fire-and-forget task that
                // throws here surfaces as an unrelated crash while the window is still being built,
                // which says nothing about what actually failed. An unattended run has to report.
                try
                {
                    var prRepo = services.GetRequiredService<IPurchaseRequisitionRepository>();
                    await DatabaseSelfCheck.RunAsync(services.GetRequiredService<SqliteDatabase>(), prRepo).ConfigureAwait(false);
                    await CallOffSelfCheck.RunAsync(services.GetRequiredService<ICallOffRepository>(), prRepo).ConfigureAwait(false);
                    CrashLog.Write("DATABASE SELF-CHECKS PASSED");
                }
                catch (Exception ex)
                {
                    CrashLog.Write("DATABASE SELF-CHECKS FAILED", ex);
                }
            }

            // The end-to-end pass over PR / RFQ / PO / merge / split / shared / combined, with
            // resource metrics. Writes to whatever database it is pointed at and cleans up after
            // itself - run it against a copy of the 20k test database, never a real one.
            //
            // On a thread-pool thread deliberately. Started bare from a UI thread, every await inside
            // resumes there too, and forty seconds of database work sits on the dispatcher - long
            // enough that the board's own delayed callbacks fire late and its self-check fails
            // against a perfectly healthy app.
            if (On("PROCURE_FLOW_SELFCHECK"))
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        await ProcurementFlowSelfCheck.RunAsync(
                            services.GetRequiredService<SqliteDatabase>(),
                            services.GetRequiredService<IPurchaseRequisitionRepository>()).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write("PROCUREMENT FLOW SELF-CHECK THREW", ex);
                    }
                }).ConfigureAwait(false);
            }
        }

        private static async Task RunTodoSelfChecksAsync(IServiceProvider services)
        {
            SelfCheckLog.Reset();
            var repo = services.GetRequiredService<ITodoRepository>();
            var errorHandler = services.GetRequiredService<Procure.Services.IErrorHandler>();

            await SweepAsync(services, todo: true).ConfigureAwait(false);

            try
            {
                await TodoRepositorySelfCheck.RunAsync(repo).ConfigureAwait(false);
                await TodoFeatureSelfCheck.RunAsync(repo, errorHandler,
                    services.GetRequiredService<Procure.Services.ILinkTargetService>()).ConfigureAwait(false);
                SelfCheckLog.Write("ALL TODO SELF-CHECKS PASSED");
            }
            catch (Exception ex)
            {
                SelfCheckLog.Write("TODO SELF-CHECKS FAILED: " + ex);
            }
        }

        private static async Task RunNoteSelfChecksAsync(IServiceProvider services)
        {
            NoteSelfCheckLog.Reset();
            var repo = services.GetRequiredService<INoteRepository>();
            var errorHandler = services.GetRequiredService<Procure.Services.IErrorHandler>();

            await SweepAsync(services, todo: false).ConfigureAwait(false);

            try
            {
                await NoteRepositorySelfCheck.RunAsync(repo).ConfigureAwait(false);
                await NoteFeatureSelfCheck.RunAsync(repo, errorHandler,
                    services.GetRequiredService<Procure.Services.ILinkTargetService>()).ConfigureAwait(false);
                await LinkTargetSelfCheck.RunAsync(
                    services.GetRequiredService<Procure.Services.ILinkTargetService>(),
                    services.GetRequiredService<IPurchaseRequisitionRepository>()).ConfigureAwait(false);
                NoteSelfCheckLog.Write("ALL NOTE SELF-CHECKS PASSED");
            }
            catch (Exception ex)
            {
                NoteSelfCheckLog.Write("NOTE SELF-CHECKS FAILED: " + ex);
            }
        }

        /// <summary>Removes rows a killed self-check run left behind, so a normal launch never shows
        /// tfsc-/nfsc- entries in the real list. One from 3 September was still sitting in the to-do
        /// list when this sweep was written.</summary>
        private static async Task SweepAsync(IServiceProvider services, bool todo)
        {
            try
            {
                if (todo)
                {
                    var repo = services.GetRequiredService<ITodoRepository>();
                    foreach (var t in (await repo.GetAllAsync().ConfigureAwait(false))
                                 .Where(t => t.Title.StartsWith("tfsc-", StringComparison.Ordinal)
                                          || t.Title.StartsWith("todo-selfcheck-", StringComparison.Ordinal)))
                        await repo.DeleteAsync(t.Id).ConfigureAwait(false);
                }
                else
                {
                    var repo = services.GetRequiredService<INoteRepository>();
                    foreach (var n in (await repo.GetListAsync().ConfigureAwait(false))
                                 .Where(n => n.Title.StartsWith("nrsc-", StringComparison.Ordinal)
                                          || n.Title.StartsWith("nfsc-", StringComparison.Ordinal)))
                        await repo.DeleteAsync(n.Id).ConfigureAwait(false);
                }
            }
            catch
            {
                // Housekeeping only.
            }
        }
    }
}
