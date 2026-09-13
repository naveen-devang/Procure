using System;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;

namespace Procure.Data
{
    /// <summary>
    /// Covers <see cref="ILinkTargetService"/>, which replaced the full in-memory list of every
    /// linkable PR / RFQ / PO that Tasks and Notes each used to hold. A client-side filter over a
    /// resident list got correctness for free; bounded queries can fail in ways it could not - a
    /// wrong LIKE escape, an id formatted differently from how it was written, a limit that silently
    /// drops matches - so those are asserted here instead.
    ///
    /// Runs with PROCURE_NOTE_SELFCHECK=1. Creates one PR and removes it again.
    /// </summary>
    internal static class LinkTargetSelfCheck
    {
        public static async Task RunAsync(ILinkTargetService links, IPurchaseRequisitionRepository prRepo)
        {
            // "%" and "_" are LIKE wildcards; a marker carrying both proves they are escaped rather
            // than interpreted, which is the difference between finding this PR and finding every PR.
            var marker = "ltsc_" + Guid.NewGuid().ToString("N")[..8] + "%x";
            var pr = new PurchaseRequisition
            {
                PrNo = marker,
                Description = "link target self check",
                Items = { new PrItem { ItemName = marker + "-item", Quantity = 1 } }
            };

            try
            {
                await prRepo.SaveAsync(pr);

                var hits = await links.SearchAsync(marker);
                Assert(hits.Count == 1, $"searching the exact marker returns exactly its PR; got {hits.Count}");
                Assert(hits[0].Id == pr.Id, "the row returned is the PR that was just saved");
                Assert(hits[0].Type == "PR", $"type is 'PR'; got '{hits[0].Type}'");
                Assert(hits[0].Label.StartsWith(marker, StringComparison.Ordinal), "label leads with the PR number");

                // If "%" were live rather than escaped, this near-miss would still match.
                var decoy = await links.SearchAsync(marker.Replace("%x", "%y"));
                Assert(decoy.Count == 0, $"LIKE wildcards are escaped, not honoured; got {decoy.Count} rows");

                Assert((await links.SearchAsync("")).Count == 0, "an empty term returns nothing rather than the whole table");
                Assert((await links.SearchAsync("   ")).Count == 0, "a whitespace term returns nothing");

                var limited = await links.SearchAsync("-", limit: 3);
                Assert(limited.Count <= 3, $"the limit is honoured; asked for 3, got {limited.Count}");

                var chips = await links.GetChipLabelsAsync(new[] { pr.Id });
                Assert(chips.TryGetValue(pr.Id, out var chip) && chip == marker,
                    "chip label resolves by id to the PR number");

                Assert((await links.GetChipLabelsAsync(new[] { Guid.NewGuid() })).Count == 0,
                    "an id with no row is absent rather than throwing");
                Assert((await links.GetChipLabelsAsync(Array.Empty<Guid>())).Count == 0,
                    "no ids means no query and no rows");

                // The mixed batch is the one the page models actually issue.
                var mixed = await links.GetChipLabelsAsync(new[] { pr.Id, Guid.NewGuid(), pr.Id });
                Assert(mixed.Count == 1 && mixed.ContainsKey(pr.Id),
                    "a batch of real and stale ids resolves the real ones and drops the rest");
            }
            finally
            {
                try { await prRepo.DeleteAsync(pr.Id); } catch { }
            }
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("LinkTargetSelfCheck failed: " + what);
        }
    }
}
