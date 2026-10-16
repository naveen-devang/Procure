using System;

namespace Procure.Models
{
    /// <summary>
    /// When a change to a quote, comparison or order is allowed to move the requisition's status, and
    /// where to. Every one of these returns null for "leave the status alone", and does so for any
    /// status the pipeline does not own: Closed, On Hold, Cancelled and Merged are decisions a person
    /// made, so no approval date or deleted order may overwrite them. Before this, a signature on an
    /// old PCR flipped a Closed requisition back to "PCR Approved".
    /// </summary>
    public static class StatusFlow
    {
        /// <summary>Statuses before an order exists, where a comparison may still set the status.</summary>
        private static bool IsBeforeOrder(string? s) =>
            s is ProcurementStatus.PrRaised or ProcurementStatus.RfqSent or ProcurementStatus.QuotesReceived
              or ProcurementStatus.PcrSubmitted or ProcurementStatus.PcrApproved;

        /// <summary>Statuses an order drives.</summary>
        private static bool IsOrderDriven(string? s) =>
            s is ProcurementStatus.PoRaised or ProcurementStatus.PartiallyDelivered or ProcurementStatus.Delivered;

        /// <summary>A comparison was created, its stages edited or an approval date changed.</summary>
        public static string? AfterPcrChange(string? current, bool fullyApproved)
        {
            if (!IsBeforeOrder(current)) return null;
            var next = fullyApproved ? ProcurementStatus.PcrApproved : ProcurementStatus.PcrSubmitted;
            return next == current ? null : next;
        }

        /// <summary>An order was added, or its delivery status changed. Orders push the requisition forward
        /// and, when they go back (Delivered to Raised), pull it back; never past a person's decision.</summary>
        public static string? AfterPoChange(string? current, int poCount, int deliveredCount)
        {
            if (poCount <= 0) return null;
            if (!IsBeforeOrder(current) && !IsOrderDriven(current)) return null;
            var next = deliveredCount >= poCount ? ProcurementStatus.Delivered
                     : deliveredCount > 0 ? ProcurementStatus.PartiallyDelivered
                     : ProcurementStatus.PoRaised;
            return next == current ? null : next;
        }

        /// <summary>A quote's received state changed. Only the two quote statuses react.</summary>
        public static string? AfterRfqChange(string? current, int rfqCount, bool allReceived)
        {
            if (current is not (ProcurementStatus.RfqSent or ProcurementStatus.QuotesReceived)) return null;
            var next = rfqCount == 0 ? ProcurementStatus.PrRaised
                     : allReceived ? ProcurementStatus.QuotesReceived
                     : ProcurementStatus.RfqSent;
            return next == current ? null : next;
        }

        /// <summary>The last order or quote of a requisition was deleted: the status that was resting on it
        /// falls back to what the requisition still has. <paramref name="orderDeleted"/> says which one went.</summary>
        public static string? AfterDelete(string? current, bool orderDeleted, int rfqCount, bool allRfqsReceived,
            bool hasPcr, bool pcrApproved, int poCount, int poDelivered)
        {
            if (orderDeleted)
            {
                if (!IsOrderDriven(current)) return null;
                if (poCount > 0) return AfterPoChange(current, poCount, poDelivered);
            }
            else if (current is not (ProcurementStatus.RfqSent or ProcurementStatus.QuotesReceived))
            {
                return null;   // a deleted quote only matters to the statuses that say "quotes"
            }

            string next;
            if (orderDeleted && hasPcr) next = pcrApproved ? ProcurementStatus.PcrApproved : ProcurementStatus.PcrSubmitted;
            else if (rfqCount > 0) next = allRfqsReceived ? ProcurementStatus.QuotesReceived : ProcurementStatus.RfqSent;
            else next = ProcurementStatus.PrRaised;
            return next == current ? null : next;
        }

        /// <summary>Self-check, run with the others (PROCURE_SELFCHECK).</summary>
        public static void SelfCheck()
        {
            static void Check(bool ok, string what)
            {
                if (!ok) throw new InvalidOperationException("StatusFlow: " + what);
            }

            // comparisons
            Check(AfterPcrChange(ProcurementStatus.PrRaised, false) == ProcurementStatus.PcrSubmitted, "new PCR on a raised PR");
            Check(AfterPcrChange(ProcurementStatus.PcrSubmitted, true) == ProcurementStatus.PcrApproved, "approved");
            Check(AfterPcrChange(ProcurementStatus.PcrApproved, false) == ProcurementStatus.PcrSubmitted, "an approval undone");
            Check(AfterPcrChange(ProcurementStatus.PcrApproved, true) is null, "no change when already there");
            foreach (var s in new[] { ProcurementStatus.PoRaised, ProcurementStatus.PartiallyDelivered, ProcurementStatus.Delivered,
                                      ProcurementStatus.Closed, ProcurementStatus.OnHold, ProcurementStatus.Cancelled, ProcurementStatus.Merged })
            {
                Check(AfterPcrChange(s, true) is null && AfterPcrChange(s, false) is null, $"a PCR never moves {s}");
            }

            // orders
            Check(AfterPoChange(ProcurementStatus.PcrApproved, 1, 0) == ProcurementStatus.PoRaised, "first order");
            Check(AfterPoChange(ProcurementStatus.PoRaised, 2, 1) == ProcurementStatus.PartiallyDelivered, "one of two delivered");
            Check(AfterPoChange(ProcurementStatus.PartiallyDelivered, 2, 2) == ProcurementStatus.Delivered, "all delivered");
            Check(AfterPoChange(ProcurementStatus.Delivered, 1, 0) == ProcurementStatus.PoRaised, "delivered set back to raised");
            Check(AfterPoChange(ProcurementStatus.Closed, 1, 1) is null && AfterPoChange(ProcurementStatus.OnHold, 1, 1) is null
                  && AfterPoChange(ProcurementStatus.Cancelled, 1, 1) is null, "orders never override a decision");
            Check(AfterPoChange(ProcurementStatus.PoRaised, 0, 0) is null, "no orders, no opinion");

            // quotes
            Check(AfterRfqChange(ProcurementStatus.RfqSent, 2, true) == ProcurementStatus.QuotesReceived, "all quotes in");
            Check(AfterRfqChange(ProcurementStatus.QuotesReceived, 2, false) == ProcurementStatus.RfqSent, "a quote set back");
            Check(AfterRfqChange(ProcurementStatus.PoRaised, 2, false) is null, "quotes do not move an ordered PR");

            // deletes
            Check(AfterDelete(ProcurementStatus.PoRaised, true, 1, true, true, true, 0, 0) == ProcurementStatus.PcrApproved, "last order gone, PCR approved");
            Check(AfterDelete(ProcurementStatus.PoRaised, true, 1, true, true, false, 0, 0) == ProcurementStatus.PcrSubmitted, "last order gone, PCR pending");
            Check(AfterDelete(ProcurementStatus.PoRaised, true, 1, true, false, false, 0, 0) == ProcurementStatus.QuotesReceived, "last order gone, quotes in");
            Check(AfterDelete(ProcurementStatus.PoRaised, true, 1, false, false, false, 0, 0) == ProcurementStatus.RfqSent, "last order gone, quote outstanding");
            Check(AfterDelete(ProcurementStatus.Delivered, true, 0, false, false, false, 0, 0) == ProcurementStatus.PrRaised, "order and quotes all gone");
            Check(AfterDelete(ProcurementStatus.PartiallyDelivered, true, 1, true, false, false, 2, 2) == ProcurementStatus.Delivered, "the undelivered order deleted");
            Check(AfterDelete(ProcurementStatus.PoRaised, true, 1, true, false, false, 1, 0) is null, "an order is left, nothing changes");
            Check(AfterDelete(ProcurementStatus.Closed, true, 0, false, false, false, 0, 0) is null, "Closed is never touched");
            Check(AfterDelete(ProcurementStatus.RfqSent, false, 0, false, false, false, 0, 0) == ProcurementStatus.PrRaised, "last quote gone");
            Check(AfterDelete(ProcurementStatus.RfqSent, false, 1, true, false, false, 0, 0) == ProcurementStatus.QuotesReceived, "the outstanding quote deleted");
            Check(AfterDelete(ProcurementStatus.PoRaised, false, 0, false, false, false, 1, 0) is null, "a deleted quote does not move an ordered PR");
        }
    }
}
