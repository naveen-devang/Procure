using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Procure.Abstractions;
using Procure.Data.Repositories;

namespace Procure.Services
{
    /// <summary>
    /// "Deleted - Undo" for PRs, RFQs, POs, tasks and notes. A delete is not run when you confirm it:
    /// the records are hidden (<see cref="PendingDeleteFilter"/>) and the real delete runs when the
    /// Undo window closes. Undo just un-hides them - nothing was written, so nothing has to be put
    /// back, and nothing can come back slightly different.
    ///
    /// The delete always happens unless Undo is pressed:
    ///  - the window runs out                      -> deleted
    ///  - another delete starts inside the window  -> the earlier one is deleted first (one Undo at a time)
    ///  - the app is closed                        -> MainWindow waits for <see cref="CommitNowAsync"/>
    ///  - the app crashes or is killed             -> the journal file names the records, and
    ///                                                <see cref="CommitLeftoversAsync"/> deletes them on the next launch
    ///
    /// The journal sits beside the database it describes, so a leftover can never be applied to a
    /// different database after the location is changed in Settings.
    /// </summary>
    public sealed class UndoDeleteService
    {
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        private const string JournalName = "pending-deletes.json";

        private readonly Func<PendingDeleteItem, Task> _delete;
        private readonly Func<string> _journalDir;
        private readonly IUiDispatcher _dispatcher;
        private readonly IErrorHandler? _errorHandler;

        private sealed record Batch(IReadOnlyList<PendingDeleteItem> Items, Action OnUndo, Action? OnCommitted);

        private Batch? _current;
        private int _generation;
        private Task _committing = Task.CompletedTask;

        /// <summary>Raised on the UI thread: a message when the Undo toast should show, null when it
        /// should go (undone, deleted, or replaced by a newer delete's toast).</summary>
        public event Action<string?>? ToastChanged;

        public UndoDeleteService(IPurchaseRequisitionRepository prRepo, ITodoRepository todoRepo,
            INoteRepository noteRepo, IUiDispatcher dispatcher, IErrorHandler errorHandler)
            : this(RepositoryDelete(prRepo, todoRepo, noteRepo),
                () => DatabaseConstants.DatabaseDirectory, dispatcher, errorHandler)
        {
        }

        /// <summary>The real delete for each kind - the same repository calls a delete made before
        /// Undo existed ran, so a PR still takes its RFQs, PCRs, POs and search row with it.</summary>
        internal static Func<PendingDeleteItem, Task> RepositoryDelete(IPurchaseRequisitionRepository prRepo,
            ITodoRepository todoRepo, INoteRepository noteRepo) => item => item.Kind switch
            {
                DeleteKind.Pr => prRepo.DeleteAsync(item.Id),
                DeleteKind.Rfq => prRepo.DeleteRfqAsync(item.Id),
                DeleteKind.Po => prRepo.DeletePoAsync(item.Id),
                DeleteKind.Task => todoRepo.DeleteAsync(item.Id),
                DeleteKind.Note => noteRepo.DeleteAsync(item.Id),
                _ => Task.CompletedTask,
            };

        /// <summary>Self-check seam: a fake delete and a scratch journal folder.</summary>
        internal UndoDeleteService(Func<PendingDeleteItem, Task> delete, Func<string> journalDir,
            IUiDispatcher dispatcher, IErrorHandler? errorHandler)
        {
            _delete = delete;
            _journalDir = journalDir;
            _dispatcher = dispatcher;
            _errorHandler = errorHandler;
        }

        /// <summary>A delete is waiting for its window to run out, or is being written right now.</summary>
        public bool IsBusy => _current is not null || !_committing.IsCompleted;

        private string JournalPath => Path.Combine(_journalDir(), JournalName);

        /// <summary>Hides <paramref name="items"/> at once and deletes them when the window closes.
        /// Call it after the "Are you sure?" box, then take the records off screen.
        /// <paramref name="onUndo"/> puts them back on screen; <paramref name="onCommitted"/> runs once
        /// the database delete has finished (dashboard totals, other pages).</summary>
        public async Task StartAsync(string message, IReadOnlyList<PendingDeleteItem> items,
            Action onUndo, Action? onCommitted = null)
        {
            await CommitNowAsync();
            if (items.Count == 0) return;

            PendingDeleteFilter.Add(items);
            _current = new Batch(items, onUndo, onCommitted);
            WriteJournal(items);

            var generation = ++_generation;
            _dispatcher.PostDelayed(Window, () =>
            {
                if (generation == _generation) _ = CommitNowAsync();
            });
            ToastChanged?.Invoke(message);
        }

        public void Undo()
        {
            var batch = _current;
            if (batch is null) return;
            _current = null;
            _generation++;

            PendingDeleteFilter.Remove(batch.Items);
            DeleteJournal();
            ToastChanged?.Invoke(null);
            batch.OnUndo();
        }

        /// <summary>Runs the waiting delete now. Returns the in-flight delete when one is already
        /// running, so closing the app always waits for it to land.</summary>
        public Task CommitNowAsync()
        {
            var batch = _current;
            if (batch is null) return _committing;
            _current = null;
            _generation++;
            ToastChanged?.Invoke(null);
            return _committing = CommitAsync(batch);
        }

        private async Task CommitAsync(Batch batch)
        {
            try
            {
                foreach (var item in batch.Items) await _delete(item);
            }
            catch (Exception ex)
            {
                // Whatever did not delete reappears once it leaves the filter below - the screen then
                // shows the truth instead of a record that looks gone but is still in the database.
                _errorHandler?.HandleError(ex);
            }
            finally
            {
                PendingDeleteFilter.Remove(batch.Items);
                DeleteJournal();
            }
            batch.OnCommitted?.Invoke();
        }

        /// <summary>Launch step: deletes what a crashed or killed session left waiting.</summary>
        public async Task CommitLeftoversAsync()
        {
            string path;
            try
            {
                path = JournalPath;
                if (!File.Exists(path)) return;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Undo journal: cannot locate", ex);
                return;
            }

            List<PendingDeleteItem>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<PendingDeleteItem>>(await File.ReadAllTextAsync(path));
            }
            catch (Exception ex)
            {
                // Unreadable means a crash mid-write, i.e. before StartAsync returned - the delete
                // never showed as done, so dropping it is the honest outcome.
                CrashLog.Write("Undo journal: unreadable, discarded", ex);
                TryDelete(path);
                return;
            }

            // A failure here keeps the journal, so the next launch tries again.
            foreach (var item in items ?? new()) await _delete(item);
            TryDelete(path);
        }

        private void WriteJournal(IReadOnlyList<PendingDeleteItem> items)
        {
            try
            {
                var path = JournalPath;
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(items));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // The Undo window still works; only the crash case loses its safety net.
                CrashLog.Write("Undo journal: write failed", ex);
            }
        }

        private void DeleteJournal()
        {
            try { TryDelete(JournalPath); }
            catch (Exception ex) { CrashLog.Write("Undo journal: cannot locate", ex); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { CrashLog.Write("Undo journal: delete failed", ex); }
        }
    }
}
