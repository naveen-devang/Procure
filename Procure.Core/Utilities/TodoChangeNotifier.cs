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
    }
}
