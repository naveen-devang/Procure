using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Procure.Data.Repositories;
using Procure.PageModels;
using Procure.Services;

namespace Procure.Data
{
    /// <summary>
    /// PROCURE_UNDO_PROBE=1 (Debug) - does the Undo window leak, and does it slow anything down?
    /// Drives the live page models and the real toast, on the 20k database:
    ///   1. delete -> Undo, every kind (PR, RFQ, PO, task, note), twice over. A leak shows as heap
    ///      that keeps climbing from round 1 to round 2; a healthy app levels off.
    ///   2. delete -> final, one after another (each delete settles the one before).
    ///   3. one delete of 1,000 selected PRs: how long the hide takes, what a board read costs with
    ///      1,000 ids left out, and how long the UI thread stalls while they are really deleted.
    /// Weak references say whether the objects a delete or Undo let go of are actually collectable.
    /// Everything it creates is tagged and removed again. Result: %TEMP%\procure-undo-probe.log.
    /// </summary>
    public static class UndoMemoryProbe
    {
        private const int Cycles = 300;
        private const int Bulk = 1000;

        public static async Task RunAsync(IServiceProvider services)
        {
            var log = new StringBuilder();
            var marker = "undoprobe-" + Guid.NewGuid().ToString("N")[..6];
            var board = services.GetRequiredService<PrListPageModel>();
            var tasks = services.GetRequiredService<TodoPageModel>();
            var notes = services.GetRequiredService<NotePageModel>();
            var undo = services.GetRequiredService<UndoDeleteService>();
            var repo = services.GetRequiredService<IPurchaseRequisitionRepository>();
            var todoRepo = services.GetRequiredService<ITodoRepository>();
            var noteRepo = services.GetRequiredService<INoteRepository>();
            var cleanupPrs = new List<Guid>();
            Guid? taskId = null, noteId = null;

            try
            {
                log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  undo probe ({marker})");

                board.BoardAppearing();
                for (var i = 0; i < 100 && board.FilteredPrs.Count == 0; i++) await Task.Delay(100);
                await tasks.LoadAsync(force: true);
                await notes.LoadListAsync(force: true);

                // ---- seed one PR with a quote and an order, one task, one note ---------------------
                var pr = NewPr(marker + "-seed");
                await repo.SaveAsync(pr);
                cleanupPrs.Add(pr.Id);
                var rfq = new RequestForQuotation { Id = Guid.NewGuid(), PrId = pr.Id, RfqNo = marker, Vendor = marker, Currency = "AED", VatType = "5%" };
                await repo.SaveRfqAsync(rfq);
                var po = new PurchaseOrder { Id = Guid.NewGuid(), PrId = pr.Id, PoNo = marker, Vendor = marker, Currency = "AED", VatType = "5%", LinkedRfqId = rfq.Id, Date = DateTime.Today };
                await repo.SavePoAsync(po);
                pr = (await repo.GetByIdsAsync(new[] { pr.Id }))[0];

                var now = DateTime.Now;
                var seedTask = new TodoTask { Title = marker, CreatedAt = now, UpdatedAt = now };
                await todoRepo.UpsertAsync(seedTask);
                taskId = seedTask.Id;
                var seedNote = new Note { Title = marker, CreatedAt = now, UpdatedAt = now };
                await noteRepo.UpsertAsync(seedNote, marker);
                noteId = seedNote.Id;
                await tasks.LoadAsync(force: true);
                await notes.LoadListAsync(force: true);

                // ---- 1. delete -> Undo --------------------------------------------------------------
                var timing = new Dictionary<string, List<double>>();
                var released = new List<WeakReference>();

                async Task Cycle(bool record)
                {
                    await Time(timing, "PR hide", record, () => board.DeletePrsWithUndoAsync(new[] { pr }, "p"));
                    Time(timing, "PR undo", record, undo.Undo);

                    var r = pr.Rfqs.FirstOrDefault(x => x.Id == rfq.Id) ?? rfq;
                    await Time(timing, "RFQ hide", record, () => board.DeleteChildWithUndoAsync(r, x => x.Id, pr.Id, DeleteKind.Rfq, "r", p => p.Rfqs, () => { }));
                    Time(timing, "RFQ undo", record, undo.Undo);

                    var o = pr.Pos.FirstOrDefault(x => x.Id == po.Id) ?? po;
                    await Time(timing, "PO hide", record, () => board.DeleteChildWithUndoAsync(o, x => x.Id, pr.Id, DeleteKind.Po, "o", p => p.Pos, () => { }));
                    Time(timing, "PO undo", record, undo.Undo);

                    // Undo re-reads the task list and note list, so each round deletes the fresh
                    // object and the one before should become garbage.
                    var t = await WaitFor(() => tasks.AllTasksForProbe.FirstOrDefault(x => x.Id == taskId));
                    released.Add(new WeakReference(t));
                    await Time(timing, "task hide", record, () => tasks.DeleteTasksWithUndoAsync(new[] { t }, "t"));
                    Time(timing, "task undo", record, undo.Undo);

                    var n = await WaitFor(() => notes.AllNotesForProbe.FirstOrDefault(x => x.Id == noteId));
                    released.Add(new WeakReference(n));
                    await Time(timing, "note hide", record, () => notes.RemoveNoteAsync(n, undoable: true));
                    Time(timing, "note undo", record, undo.Undo);
                }

                var logPath = Path.Combine(Path.GetTempPath(), "procure-undo-probe.log");
                var clock = Stopwatch.StartNew();
                void Progress(string what) => File.WriteAllText(logPath + ".progress", $"{what} at {clock.Elapsed.TotalSeconds:F0} s");
                for (var i = 0; i < 20; i++) { await Cycle(record: false); Progress($"warm-up {i + 1}"); }   // warm-up: JIT, first-use caches
                await Task.Delay(11000);                                     // let the warm-up's 10 s timers fire
                var gc0 = GcCounts();
                var heap0 = Settle();
                var ws0 = WorkingSet();
                log.AppendLine();
                log.AppendLine($"baseline after warm-up     heap {Mb(heap0),7:F1} MB   working set {Mb(ws0),6:F0} MB");

                for (var i = 0; i < Cycles; i++) { await Cycle(record: true); if (i % 20 == 0) Progress($"round 1 cycle {i}"); }
                await Task.Delay(11000);
                var wsUnforced1 = WorkingSet();
                var heap1 = Settle();
                var ws1 = WorkingSet();
                log.AppendLine($"after {Cycles} x 5 delete+undo   heap {Mb(heap1),7:F1} MB   working set {Mb(ws1),6:F0} MB (before forced GC {Mb(wsUnforced1):F0})");

                for (var i = 0; i < Cycles; i++) { await Cycle(record: false); if (i % 20 == 0) Progress($"round 2 cycle {i}"); }
                await Task.Delay(11000);
                var heap2 = Settle();
                var ws2 = WorkingSet();
                log.AppendLine($"after another {Cycles} x 5       heap {Mb(heap2),7:F1} MB   working set {Mb(ws2),6:F0} MB");
                log.AppendLine($"  heap per cycle, round 1: {(heap1 - heap0) / (double)Cycles / 1024:F2} KB   round 2: {(heap2 - heap1) / (double)Cycles / 1024:F2} KB");

                // The newest task/note objects are still on screen; everything older must be collectable.
                var stillAlive = released.Take(released.Count - 2).Count(w => w.IsAlive);
                log.AppendLine($"  replaced task/note objects still alive after GC: {stillAlive} of {released.Count - 2}");
                log.AppendLine($"  hidden ids left behind: {(PendingDeleteFilter.IsEmpty ? "none" : "SOME")}   service busy: {undo.IsBusy}");

                log.AppendLine();
                log.AppendLine("operation        median ms   p95 ms   max ms   (UI thread, 300 each)");
                foreach (var (name, list) in timing)
                {
                    list.Sort();
                    log.AppendLine($"{name,-16} {list[list.Count / 2],9:F2} {list[(int)(list.Count * 0.95)],8:F2} {list[^1],8:F2}");
                }

                // ---- 2. delete -> final, back to back ----------------------------------------------
                var serial = Enumerable.Range(0, Cycles).Select(i => NewPr($"{marker}-serial-{i}")).ToList();
                await repo.SaveBatchPrsAsync(serial);
                cleanupPrs.AddRange(serial.Select(p => p.Id));
                var serialRefs = serial.Select(p => new WeakReference(p)).ToList();
                var serialTimes = new List<double>();
                foreach (var p in serial)
                {
                    var sw = Stopwatch.StartNew();
                    await board.DeletePrsWithUndoAsync(new[] { p }, "s");   // settles the previous one first
                    serialTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                await undo.CommitNowAsync();
                serial.Clear();
                await Task.Delay(11000);
                var heap3 = Settle();
                var gone = (await repo.GetByIdsAsync(cleanupPrs.Skip(1).ToList())).Count == 0;
                serialTimes.Sort();
                log.AppendLine();
                log.AppendLine($"{Cycles} deletes back to back, each settling the last: median {serialTimes[serialTimes.Count / 2]:F1} ms, max {serialTimes[^1]:F1} ms per delete");
                log.AppendLine($"  all {Cycles} really deleted: {gone}   PR objects still alive after GC: {serialRefs.Count(w => w.IsAlive)} of {Cycles}   heap {Mb(heap3):F1} MB");
                cleanupPrs.RemoveRange(1, cleanupPrs.Count - 1);

                // ---- 3. one delete of 1,000 PRs ----------------------------------------------------
                var bulk = Enumerable.Range(0, Bulk).Select(i => NewPr($"{marker}-bulk-{i}")).ToList();
                await repo.SaveBatchPrsAsync(bulk);
                cleanupPrs.AddRange(bulk.Select(p => p.Id));
                var query = new PrQuery(null, null, false, false, false, 10, 5, 0, 50);
                var readPlain = await TimeRead(repo, query);

                var hideSw = Stopwatch.StartNew();
                await board.DeletePrsWithUndoAsync(bulk, "b");
                hideSw.Stop();
                var readHidden = await TimeRead(repo, query);
                var searchHidden = await TimeRead(repo, query with { Search = marker });

                var commitSw = Stopwatch.StartNew();
                var commit = undo.CommitNowAsync();
                var worstGap = 0.0;
                var tick = Stopwatch.StartNew();
                while (!commit.IsCompleted)
                {
                    await Task.Delay(15);   // resumes on the UI thread: a late resume is a stall
                    worstGap = Math.Max(worstGap, tick.Elapsed.TotalMilliseconds - 15);
                    tick.Restart();
                }
                await commit;
                commitSw.Stop();
                var bulkGone = (await repo.GetByIdsAsync(bulk.Select(p => p.Id).ToList())).Count == 0;
                cleanupPrs.RemoveRange(1, cleanupPrs.Count - 1);
                var bulkRefs = bulk.Select(p => new WeakReference(p)).ToList();
                bulk.Clear();
                await Task.Delay(11000);
                var heap4 = Settle();
                var ws4 = WorkingSet();
                var gc1 = GcCounts();

                log.AppendLine();
                log.AppendLine($"delete {Bulk} selected PRs at once:");
                log.AppendLine($"  hide (UI thread, incl. board reload kick-off): {hideSw.Elapsed.TotalMilliseconds:F0} ms");
                log.AppendLine($"  board page read: {readPlain:F1} ms normally, {readHidden:F1} ms with {Bulk} ids left out; search {searchHidden:F1} ms");
                log.AppendLine($"  final delete of all {Bulk}: {commitSw.Elapsed.TotalSeconds:F1} s, worst UI-thread stall {worstGap:F0} ms, all deleted: {bulkGone}");
                log.AppendLine($"  PR objects still alive after GC: {bulkRefs.Count(w => w.IsAlive)} of {Bulk}   heap {Mb(heap4):F1} MB   working set {Mb(ws4):F0} MB");

                log.AppendLine();
                log.AppendLine($"collections during the probe: gen0 {gc1.g0 - gc0.g0}, gen1 {gc1.g1 - gc0.g1}, gen2 {gc1.g2 - gc0.g2} (forced settles included)");
                log.AppendLine($"GC pause time over the whole process: {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms");
            }
            catch (Exception ex)
            {
                log.AppendLine("PROBE FAILED: " + ex);
            }
            finally
            {
                try
                {
                    await undo.CommitNowAsync();
                    foreach (var id in cleanupPrs) await repo.DeleteAsync(id);
                    if (taskId is { } t) await todoRepo.DeleteAsync(t);
                    if (noteId is { } n) await noteRepo.DeleteAsync(n);
                }
                catch (Exception ex) { log.AppendLine("cleanup failed: " + ex.Message); }
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "procure-undo-probe.log"), log.AppendLine().AppendLine("DONE").ToString());
            }
        }

        private static PurchaseRequisition NewPr(string no) => new()
        {
            Id = Guid.NewGuid(),
            PrNo = no,
            Description = no,
            Requestor = "undo probe",
            Plant = ProcurementPlant.RW01,
            PrType = ProcurementPrType.StoresAndSpares,
        };

        private static async Task Time(Dictionary<string, List<double>> into, string name, bool record, Func<Task> op)
        {
            var sw = Stopwatch.StartNew();
            await op();
            if (record) (into.TryGetValue(name, out var l) ? l : into[name] = new()).Add(sw.Elapsed.TotalMilliseconds);
        }

        private static void Time(Dictionary<string, List<double>> into, string name, bool record, Action op)
        {
            var sw = Stopwatch.StartNew();
            op();
            if (record) (into.TryGetValue(name, out var l) ? l : into[name] = new()).Add(sw.Elapsed.TotalMilliseconds);
        }

        private static async Task<double> TimeRead(IPurchaseRequisitionRepository repo, PrQuery q)
        {
            await repo.GetPageAsync(q);   // warm
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++) await repo.GetPageAsync(q);
            return sw.Elapsed.TotalMilliseconds / 5;
        }

        private static async Task<T> WaitFor<T>(Func<T?> find) where T : class
        {
            for (var i = 0; i < 200; i++)
            {
                if (find() is { } found) return found;
                await Task.Delay(10);
            }
            throw new TimeoutException("the undone record did not come back");
        }

        private static (int g0, int g1, int g2) GcCounts() => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        private static long Settle()
        {
            for (var i = 0; i < 2; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        private static long WorkingSet()
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return p.WorkingSet64;
        }

        private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);
    }
}
