using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.PageModels
{
    // Last price filled in for you. The PR is where it belongs: a PR line's estimated price is what
    // every RFQ copies as its Last price, what editing a PR pushes into open quotes, and what the PCR
    // prints - so filling it there reaches all of them. The RFQ-side fill below stays only as a safety
    // net for PRs made before this existed. Either way: what the item was last bought for, from the
    // newest PO line with the same name; a price someone typed is never replaced.
    public partial class PrListPageModel
    {
        /// <summary>Fills PR lines' estimated price from past POs: lines with a name and no price, and
        /// lines whose filled price was for a name they no longer have. Called when an item name box
        /// loses focus, after a paste, and when Edit PR opens. <paramref name="ownPrId"/> is the PR
        /// being edited - its own POs are not a "last" price for it.</summary>
        public async Task FillPrItemPricesAsync(IReadOnlyList<PrItem> items, Guid? ownPrId)
        {
            static bool Wants(PrItem i, string name) =>
                name.Length > 0 &&
                (i.EstimatedUnitPrice is null ||
                 (i.HasEstimatedPriceSource && !string.Equals(i.PriceFilledForName, name, StringComparison.OrdinalIgnoreCase)));

            var wanting = items.Select(i => (Item: i, Name: i.ItemName?.Trim() ?? string.Empty))
                               .Where(w => Wants(w.Item, w.Name))
                               .ToList();
            if (wanting.Count == 0) return;

            try
            {
                var exclude = ownPrId is { } id ? new[] { id } : Array.Empty<Guid>();
                var names = wanting.Select(w => w.Name).ToList();
                var prices = await Task.Run(() => _prRepo.GetLastPaidPricesAsync(names, exclude));

                foreach (var (item, name) in wanting)
                {
                    // Re-checked on arrival: the user may have typed a price or a new name meanwhile.
                    if (!string.Equals(item.ItemName?.Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Wants(item, name)) continue;

                    if (prices.TryGetValue(name, out var paid)) item.FillEstimatedPrice(paid, name);
                    else if (item.HasEstimatedPriceSource) item.ClearFilledEstimate();
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        private int _lastPriceGeneration;

        /// <summary>Looks the prices up off the UI thread and fills them when they arrive, so the form
        /// opens at once. <paramref name="excludePrIds"/> are the quote's own requisitions.</summary>
        internal async Task FillLastPricesAsync(IReadOnlyList<RfqItem> lines, IReadOnlyCollection<Guid> excludePrIds)
        {
            var generation = ++_lastPriceGeneration;
            var wanting = lines.Where(l => l.LastPrice is null && !string.IsNullOrWhiteSpace(l.ItemName))
                               .Select(l => (Line: l, Name: l.ItemName.Trim()))
                               .ToList();
            if (wanting.Count == 0) return;

            try
            {
                var names = wanting.Select(w => w.Name).ToList();
                var prices = await Task.Run(() => _prRepo.GetLastPaidPricesAsync(names, excludePrIds));
                if (generation != _lastPriceGeneration) return;   // the form was closed or reopened meanwhile

                foreach (var (line, name) in wanting)
                {
                    // Skip a line the user has priced or renamed while the lookup ran.
                    if (line.LastPrice is not null) continue;
                    if (!string.Equals(line.ItemName?.Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (prices.TryGetValue(name, out var paid)) line.FillLastPrice(paid);
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }
    }
}
