using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Procure.Abstractions;

namespace Procure.Services;

/// <summary>PROCURE_UNDO_SELFCHECK=1 - the Undo window's promises, with a fake delete and a scratch
/// journal folder: Undo deletes nothing, the window running out deletes, a newer delete settles the
/// older one first, a stale timer can't delete the newer one, and a crashed session's delete is
/// finished on the next launch. Result: %TEMP%\procure-undo-selfcheck.log.</summary>
internal static class UndoDeleteServiceSelfCheck
{
    private sealed class ManualDispatcher : IUiDispatcher
    {
        public readonly List<Action> Timers = new();
        public bool IsMainThread => true;
        public void Post(Action action) => action();
        public void PostDelayed(TimeSpan delay, Action action) => Timers.Add(action);
    }

    public static async Task RunAsync()
    {
        var log = Path.Combine(Path.GetTempPath(), "procure-undo-selfcheck.log");
        var dir = Path.Combine(Path.GetTempPath(), "procure-undo-selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var journal = Path.Combine(dir, "pending-deletes.json");
        try
        {
            var deleted = new List<Guid>();
            var clock = new ManualDispatcher();
            var svc = new UndoDeleteService(i => { deleted.Add(i.Id); return Task.CompletedTask; }, () => dir, clock, null);
            string? toast = "unset";
            svc.ToastChanged += m => toast = m;

            // 1. Start hides, journals, deletes nothing.
            var a = Guid.NewGuid();
            var undone = false;
            var committed = false;
            await svc.StartAsync("A deleted", new[] { new PendingDeleteItem(DeleteKind.Pr, a) },
                () => undone = true, () => committed = true);
            Assert(PendingDeleteFilter.Contains(a), "a started delete is hidden");
            Assert(File.Exists(journal), "a started delete is journalled");
            Assert(deleted.Count == 0, "nothing is deleted while Undo is open");
            Assert(toast == "A deleted" && svc.IsBusy, "the toast shows and the service is busy");

            // 2. Undo: back on screen, nothing deleted, journal gone.
            svc.Undo();
            Assert(undone && !committed, "Undo runs the restore, not the commit");
            Assert(!PendingDeleteFilter.Contains(a) && !File.Exists(journal), "Undo un-hides and clears the journal");
            Assert(deleted.Count == 0 && toast is null && !svc.IsBusy, "Undo deletes nothing and hides the toast");
            clock.Timers[0]();   // the undone delete's timer firing late must do nothing
            await svc.CommitNowAsync();
            Assert(deleted.Count == 0, "an undone delete's timer deletes nothing");

            // 3. The window running out deletes.
            var b = Guid.NewGuid();
            await svc.StartAsync("B deleted", new[] { new PendingDeleteItem(DeleteKind.Task, b) }, () => { }, () => committed = true);
            clock.Timers[^1]();
            await svc.CommitNowAsync();
            Assert(deleted.Contains(b) && committed, "the window running out deletes and reports it");
            Assert(!PendingDeleteFilter.Contains(b) && !File.Exists(journal) && !svc.IsBusy, "a finished delete leaves no trace");

            // 4. A newer delete settles the older one first; the older timer can't touch the newer.
            var c = Guid.NewGuid();
            var d = Guid.NewGuid();
            await svc.StartAsync("C", new[] { new PendingDeleteItem(DeleteKind.Note, c) }, () => { });
            var cTimer = clock.Timers[^1];
            await svc.StartAsync("D", new[] { new PendingDeleteItem(DeleteKind.Note, d) }, () => { });
            Assert(deleted.Contains(c), "starting a second delete finishes the first");
            cTimer();
            await Task.Yield();
            Assert(!deleted.Contains(d) && PendingDeleteFilter.Contains(d), "the first delete's timer leaves the second alone");

            // 5. Crash with D waiting: the next launch finishes it.
            PendingDeleteFilter.Remove(new[] { new PendingDeleteItem(DeleteKind.Note, d) });   // a new process starts empty
            var relaunched = new List<Guid>();
            var next = new UndoDeleteService(i => { relaunched.Add(i.Id); return Task.CompletedTask; }, () => dir, new ManualDispatcher(), null);
            await next.CommitLeftoversAsync();
            Assert(relaunched.Count == 1 && relaunched[0] == d, "a crashed session's delete is finished on the next launch");
            Assert(!File.Exists(journal), "the journal is cleared once finished");
            await next.CommitLeftoversAsync();
            Assert(relaunched.Count == 1, "a launch with no journal deletes nothing");

            // 6. A failed delete on relaunch keeps the journal for the launch after.
            await svc.StartAsync("E", new[] { new PendingDeleteItem(DeleteKind.Po, Guid.NewGuid()) }, () => { });
            var failing = new UndoDeleteService(_ => throw new IOException("locked"), () => dir, new ManualDispatcher(), null);
            try { await failing.CommitLeftoversAsync(); } catch (IOException) { }
            Assert(File.Exists(journal), "a leftover that could not be deleted is kept for the next launch");
            svc.Undo();

            File.WriteAllText(log, $"PASS {DateTime.Now:u}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, $"FAIL {DateTime.Now:u}\n{ex}\n");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("UNDO: " + message);
    }
}
