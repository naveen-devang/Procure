using System;
using System.Collections.Generic;
using System.Linq;

namespace Procure.Utilities
{
    /// <summary>
    /// Keeping a shared RFQ in the same order on every PR it is on. On another PR, the copies of the
    /// shared RFQs that were just reordered are rearranged to match, in the places those copies
    /// already take; that PR's own RFQs (and shared RFQs not involved) do not move.
    ///   PR A: X, Y   ->  Y, X
    ///   PR B: X, Z, Y  ->  Y, Z, X
    /// </summary>
    public static class RfqOrder
    {
        /// <param name="rows">The other PR's RFQs top to bottom, with the RFQ number of the shared ones.</param>
        /// <param name="rank">Shared RFQ number -> its position in the order just set.</param>
        public static List<Guid> AlignShared(IReadOnlyList<(Guid Id, string? No)> rows, IReadOnlyDictionary<string, int> rank)
        {
            var slots = new List<int>();
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].No is { } no && rank.ContainsKey(no)) slots.Add(i);

            var inOrder = slots.Select(i => rows[i]).OrderBy(r => rank[r.No!]).ToList();   // stable
            var result = rows.Select(r => r.Id).ToList();
            for (var k = 0; k < slots.Count; k++) result[slots[k]] = inOrder[k].Id;
            return result;
        }

        public static void SelfCheck()
        {
            static void Check(bool ok, string what)
            {
                if (!ok) throw new InvalidOperationException("RfqOrder: " + what);
            }

            Guid x = Guid.NewGuid(), y = Guid.NewGuid(), z = Guid.NewGuid(), w = Guid.NewGuid();
            var rank = new Dictionary<string, int> { ["Y"] = 0, ["X"] = 1 };

            var b = AlignShared(new (Guid, string?)[] { (x, "X"), (z, null), (y, "Y") }, rank);
            Check(b.SequenceEqual(new[] { y, z, x }), "shared ones swap around the PR's own RFQ, which stays put");

            var unrelated = AlignShared(new (Guid, string?)[] { (w, "W"), (x, "X"), (z, null) }, rank);
            Check(unrelated.SequenceEqual(new[] { w, x, z }), "a shared RFQ not being reordered stays put; one copy alone has nothing to swap");

            var already = AlignShared(new (Guid, string?)[] { (y, "Y"), (x, "X") }, rank);
            Check(already.SequenceEqual(new[] { y, x }), "already in order: nothing changes");

            Check(AlignShared(Array.Empty<(Guid, string?)>(), rank).Count == 0, "no RFQs");
        }
    }
}
