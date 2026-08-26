using System;

namespace Procure.Utilities
{
    // Tells the call-off tracker its cached PO/PO-item data is stale whenever the PR Board
    // creates, edits, or deletes a PO - the only writes that change what that tab reads.
    public static class PoChangeNotifier
    {
        public static event Action? Changed;

        public static void NotifyChanged() => Changed?.Invoke();
    }
}
