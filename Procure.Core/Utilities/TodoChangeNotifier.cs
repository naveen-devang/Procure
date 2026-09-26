using System;

namespace Procure.Utilities
{
    // Keeps the two sides of task linking in sync in real time: the Tasks page and the
    // "Tasks (n)" strip on an expanded PR detail panel. Either side raises this after a task is
    // added, completed, renamed, linked, unlinked or deleted; the other side reloads.
    public static class TodoChangeNotifier
    {
        public static event Action? Changed;

        public static void NotifyChanged() => Changed?.Invoke();

        /// <summary>Any task write at all - including the Tasks page's own autosaves, which do not
        /// raise <see cref="Changed"/>. The reminder service re-reads its next reminder on this.
        /// Raised on whatever thread wrote.</summary>
        public static event Action? Written;

        public static void NotifyWritten() => Written?.Invoke();
    }
}
