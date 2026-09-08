using System;

namespace Procure.Utilities
{
    /// <summary>What kind of record a write touched. A listener that only cares about orders can
    /// ignore the rest instead of re-running a full query for every PR rename.</summary>
    [Flags]
    public enum ProcurementChange
    {
        None = 0,
        Pr = 1,
        Rfq = 2,
        Po = 4,
        All = Pr | Rfq | Po
    }

    /// <summary>
    /// One "something was written" signal for the whole app. Started life as PoChangeNotifier, which
    /// told the Raw &amp; Packing tab its cached PO data was stale; the tabs that read PR and RFQ data
    /// had no equivalent, so a requisition edited on the board did not reach an already-open
    /// Dashboard until it was left and revisited.
    ///
    /// Deliberately a static event with no payload beyond the flags: listeners are the page models,
    /// which are DI singletons that already unsubscribe in Dispose, and passing the changed rows
    /// around would mean holding them alive past the write. A listener re-reads what it needs.
    ///
    /// Raised after the write commits, on whatever thread committed it - listeners that touch UI
    /// state must marshal (see the page models' MainThread hop).
    /// </summary>
    public static class DataChangeNotifier
    {
        public static event Action<ProcurementChange>? Changed;

        public static void Notify(ProcurementChange what) => Changed?.Invoke(what);

        /// <summary>Kept because the PO path is by far the most common caller.</summary>
        public static void NotifyPoChanged() => Notify(ProcurementChange.Po);
    }
}
