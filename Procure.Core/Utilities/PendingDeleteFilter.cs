using System;
using System.Collections.Generic;
using System.Linq;

namespace Procure.Utilities
{
    public enum DeleteKind { Pr, Rfq, Po, Task, Note }

    public sealed record PendingDeleteItem(DeleteKind Kind, Guid Id);

    /// <summary>
    /// The records a delete is waiting on during its Undo window: still in the database, but every
    /// read that feeds a screen leaves them out, so a board reload, a Tasks refresh or a search can't
    /// bring a "deleted" row back before the delete is final. Static because the repositories read it
    /// on background threads and are built by hand in the self-checks; UndoDeleteService is the only
    /// writer. Empty almost always, so every read checks <see cref="IsEmpty"/> first and pays nothing.
    /// </summary>
    public static class PendingDeleteFilter
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<Guid, DeleteKind> _ids = new();

        public static bool IsEmpty
        {
            get { lock (Gate) return _ids.Count == 0; }
        }

        public static bool Contains(Guid id)
        {
            lock (Gate) return _ids.ContainsKey(id);
        }

        public static List<Guid> IdsOf(DeleteKind kind)
        {
            lock (Gate) return _ids.Where(p => p.Value == kind).Select(p => p.Key).ToList();
        }

        internal static void Add(IEnumerable<PendingDeleteItem> items)
        {
            var list = items.ToList();
            lock (Gate) foreach (var i in list) _ids[i.Id] = i.Kind;
            // A task hidden for Undo loses its reminder card at once, not when the delete is final.
            if (list.Any(i => i.Kind == DeleteKind.Task)) TodoChangeNotifier.NotifyWritten();
        }

        internal static void Remove(IEnumerable<PendingDeleteItem> items)
        {
            lock (Gate) foreach (var i in items) _ids.Remove(i.Id);
        }
    }
}
