using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Procure.Utilities
{
    /// <summary>
    /// Guards <see cref="UpdateDownloadCoordinator"/>'s single-flight rule: the startup
    /// auto-download and the Settings button must never run two Velopack downloads at once.
    ///
    /// Run by launching a Debug build with PROCURE_SELFCHECK=1. Nothing outside this class is
    /// touched.
    /// </summary>
    internal static class UpdateDownloadCoordinatorSelfCheck
    {
        public static void Run()
        {
            try
            {
                ConcurrentCallsShareOneDownload();
                ProgressAndCompletion();
                FailurePropagatesAndCanRetry();
                CompletedTagDoesNotReRun();
                Debug.WriteLine("UPDATE DOWNLOAD COORDINATOR SELF-CHECKS PASSED");
                CrashLog.Write("UPDATE DOWNLOAD COORDINATOR SELF-CHECKS PASSED");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("UPDATE DOWNLOAD COORDINATOR SELF-CHECKS FAILED: " + ex);
                CrashLog.Write("UPDATE DOWNLOAD COORDINATOR SELF-CHECKS FAILED", ex);
                throw;
            }
        }

        private static void ConcurrentCallsShareOneDownload()
        {
            var c = new UpdateDownloadCoordinator();
            int calls = 0;
            var release = new TaskCompletionSource();

            Func<IProgress<double>, CancellationToken, Task> work = async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                await release.Task.ConfigureAwait(false);
            };

            var t1 = c.RunAsync("v1", work);
            var t2 = c.RunAsync("v1", work);
            var t3 = c.RunAsync("v1", work);

            Assert(c.Status == UpdateDownloadStatus.Running, "status is Running while in flight");
            Assert(!t1.IsCompleted, "the download is still pending");

            release.SetResult();
            Assert(Task.WaitAll(new[] { t1, t2, t3 }, 3000), "all three callers complete");
            Assert(calls == 1, $"the work delegate ran exactly once; got {calls}");
            Assert(c.Status == UpdateDownloadStatus.Done, "status is Done afterward");
        }

        private static void ProgressAndCompletion()
        {
            var c = new UpdateDownloadCoordinator();
            int changes = 0;
            c.Changed += (_, _) => Interlocked.Increment(ref changes);

            c.RunAsync("v2", (p, _) =>
            {
                p.Report(0.5);
                p.Report(1.0);
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();

            Assert(c.Progress == 1.0, $"progress ends at 1.0; got {c.Progress}");
            Assert(c.Status == UpdateDownloadStatus.Done, "status Done");
            Assert(changes >= 2, $"Changed fired for status transitions; got {changes}");
        }

        private static void FailurePropagatesAndCanRetry()
        {
            var c = new UpdateDownloadCoordinator();

            var faulted = false;
            try
            {
                c.RunAsync("v3", (_, _) => throw new InvalidOperationException("boom")).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                faulted = true;
            }

            Assert(faulted, "the caller sees the exception");
            Assert(c.Status == UpdateDownloadStatus.Failed, "status is Failed");
            Assert(c.Error == "boom", $"error message is kept; got '{c.Error}'");

            c.Reset();
            Assert(c.Status == UpdateDownloadStatus.Idle, "Reset returns to Idle");

            int retryCalls = 0;
            c.RunAsync("v3", (_, _) => { retryCalls++; return Task.CompletedTask; }).GetAwaiter().GetResult();
            Assert(retryCalls == 1, "a retry after Reset actually runs");
            Assert(c.Status == UpdateDownloadStatus.Done, "retry completes");
        }

        private static void CompletedTagDoesNotReRun()
        {
            var c = new UpdateDownloadCoordinator();
            int calls = 0;
            Func<IProgress<double>, CancellationToken, Task> work = (_, _) => { calls++; return Task.CompletedTask; };

            c.RunAsync("v4", work).GetAwaiter().GetResult();
            c.RunAsync("v4", work).GetAwaiter().GetResult();
            c.RunAsync("v4", work).GetAwaiter().GetResult();

            Assert(calls == 1, $"a finished download for the same tag is not repeated; got {calls}");

            c.RunAsync("v5", work).GetAwaiter().GetResult();
            Assert(calls == 2, "a different tag does run");
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("UpdateDownloadCoordinator: " + what);
        }
    }
}
