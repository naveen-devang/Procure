using System;
using System.Collections.Generic;
using System.Linq;
using Procure.Models;

namespace Procure.Utilities
{
    /// <summary>
    /// The one place that answers "which PR line does this quote/PO line belong to?".
    ///
    /// Every screen used to answer it inline as <c>PrItemId == x || ItemName matches</c>. That flat OR
    /// has two failure modes, both of which reached the PO wizard as wrong quantities: a correctly
    /// linked line was ALSO claimed by any same-named sibling, and a set of same-named PR lines all
    /// collapsed onto whichever one came first (a merged PR with 12 NOS and 33 NOS of one item showed
    /// 12 and 12, then refused the PO as over-allocated).
    ///
    /// The rules here, in order:
    ///   1. A saved <see cref="IPrLine.PrItemId"/> always wins. It is the record of intent; a name
    ///      collision must never beat it.
    ///   2. Unlinked lines are matched by name one at a time, each claiming a distinct PR line that
    ///      nothing else in the same source collection has taken.
    ///   3. If every same-named PR line is already claimed, the line falls back to the first name
    ///      match rather than vanishing - an extra quote line for an item IS over-allocation, and the
    ///      allocation check should say so rather than silently ignore the quantity.
    ///
    /// "Same source collection" means one RFQ's items, or one PO's items. Claiming is deliberately
    /// NOT global: separate vendors quote the same PR line, and separate POs each order part of it.
    ///
    /// Allocation notes, because this runs on the board's reload path once per PR per refresh:
    /// nothing is allocated for the common case of a fully linked collection beyond the result
    /// dictionary itself, and the aggregate helpers reuse one dictionary and one scratch array
    /// across every PO rather than allocating per PO. PR lines are scanned linearly - a requisition
    /// has tens of lines, not thousands, and a per-call lookup dictionary cost more than it saved.
    /// </summary>
    public static class PrLineMatcher
    {
        /// <summary>Pairs one source collection's lines to PR lines. Lines with no match are absent
        /// from the dictionary rather than mapped to null.</summary>
        public static Dictionary<T, PrItem> Map<T>(IEnumerable<T>? lines, IEnumerable<PrItem>? prItems)
            where T : class, IPrLine
        {
            var result = new Dictionary<T, PrItem>();
            var targets = AsList(prItems);
            if (lines != null && targets.Count > 0)
            {
                MapInto(lines, targets, result, null);
            }
            return result;
        }

        /// <summary>The pairing itself, writing into caller-owned buffers so a loop over many POs
        /// allocates once rather than once per PO. <paramref name="into"/> is cleared first;
        /// <paramref name="claimedScratch"/> may be null (one is allocated) but must be at least
        /// <c>targets.Count</c> long when supplied.</summary>
        public static void MapInto<T>(
            IEnumerable<T> lines,
            IList<PrItem> targets,
            Dictionary<T, PrItem> into,
            bool[]? claimedScratch)
            where T : class, IPrLine
        {
            into.Clear();
            if (targets.Count == 0) return;

            var claimed = claimedScratch is { } s && s.Length >= targets.Count ? s : new bool[targets.Count];
            Array.Clear(claimed, 0, targets.Count);

            // Pass 1 - saved links. Done first so a linked line's PR item is off the table before any
            // name matching starts, whatever order the lines are in. `pending` stays null unless an
            // unlinked line actually turns up, which after the v14 repair is the uncommon case.
            List<T>? pending = null;
            foreach (var line in lines)
            {
                if (line == null) continue;

                var linkedIndex = line.PrItemId.HasValue ? IndexOfId(targets, line.PrItemId.Value) : -1;
                if (linkedIndex >= 0)
                {
                    into[line] = targets[linkedIndex];
                    claimed[linkedIndex] = true;
                }
                else
                {
                    (pending ??= new List<T>()).Add(line);
                }
            }

            if (pending == null) return;

            // Pass 2 - unlinked lines, in their own order, taking distinct PR lines.
            foreach (var line in pending)
            {
                var name = line.ItemName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                int free = -1, any = -1;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (!NameEquals(targets[i].ItemName, name)) continue;
                    if (any < 0) any = i;
                    if (!claimed[i]) { free = i; break; }
                }

                var chosen = free >= 0 ? free : any;
                if (chosen < 0) continue;

                into[line] = targets[chosen];
                claimed[chosen] = true;
            }
        }

        /// <summary>The PR line a single line belongs to, resolved against its own siblings so the
        /// one-to-one claiming above still applies. <paramref name="siblings"/> is the whole source
        /// collection the line came from.</summary>
        public static PrItem? MatchOne<T>(T line, IEnumerable<T>? siblings, IEnumerable<PrItem>? prItems)
            where T : class, IPrLine
            => line != null && Map(siblings, prItems).TryGetValue(line, out var hit) ? hit : null;

        /// <summary>Total quantity the given POs have ordered against each PR line, keyed by PR line
        /// id. Every PR line gets an entry, so callers can index without a null check.
        ///
        /// Each PO is mapped separately: two POs legitimately order against the same PR line, and
        /// summing them is the point - it is only within one PO that two lines must not both claim
        /// the same PR line.</summary>
        public static Dictionary<Guid, decimal> OrderedQuantities(
            IEnumerable<PrItem>? prItems,
            IEnumerable<PurchaseOrder>? pos,
            Func<PurchaseOrder, bool>? include = null)
        {
            var targets = AsList(prItems);
            var totals = new Dictionary<Guid, decimal>(targets.Count);
            for (int i = 0; i < targets.Count; i++) totals[targets[i].Id] = 0m;

            if (pos == null || targets.Count == 0) return totals;

            // One buffer pair for the whole loop instead of a fresh dictionary per PO.
            var buffer = new Dictionary<PurchaseOrderItem, PrItem>();
            var claimed = new bool[targets.Count];

            foreach (var po in pos)
            {
                if (po?.Items == null || po.Items.Count == 0) continue;
                if (include != null && !include(po)) continue;

                MapInto(po.Items, targets, buffer, claimed);
                foreach (var pair in buffer)
                {
                    totals[pair.Value.Id] += pair.Key.Quantity;
                }
            }

            return totals;
        }

        /// <summary>Same shape as <see cref="OrderedQuantities"/>, for lines that are not yet a PO -
        /// the quote cards sitting in the PO wizard. Each group is one source collection.</summary>
        public static Dictionary<Guid, decimal> PendingQuantities<T>(
            IEnumerable<PrItem>? prItems,
            IEnumerable<IEnumerable<T>?> lineGroups)
            where T : class, IPrLine, IQuantified
        {
            var targets = AsList(prItems);
            var totals = new Dictionary<Guid, decimal>(targets.Count);
            for (int i = 0; i < targets.Count; i++) totals[targets[i].Id] = 0m;

            if (targets.Count == 0) return totals;

            var buffer = new Dictionary<T, PrItem>();
            var claimed = new bool[targets.Count];

            foreach (var group in lineGroups)
            {
                if (group == null) continue;
                MapInto(group, targets, buffer, claimed);
                foreach (var pair in buffer)
                {
                    totals[pair.Value.Id] += pair.Key.Quantity;
                }
            }

            return totals;
        }

        public static bool NameEquals(string? a, string? b)
            => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static IList<PrItem> AsList(IEnumerable<PrItem>? items)
            => items as IList<PrItem> ?? (IList<PrItem>?)items?.ToList() ?? Array.Empty<PrItem>();

        private static int IndexOfId(IList<PrItem> targets, Guid id)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Id == id) return i;
            }
            return -1;
        }
    }

    /// <summary>A line that may point back at a PR line. Implemented by RfqItem, PurchaseOrderItem
    /// and the PO wizard's own row model so all three go through <see cref="PrLineMatcher"/>.</summary>
    public interface IPrLine
    {
        Guid? PrItemId { get; }
        string ItemName { get; }
    }

    public interface IQuantified
    {
        decimal Quantity { get; }
    }
}
