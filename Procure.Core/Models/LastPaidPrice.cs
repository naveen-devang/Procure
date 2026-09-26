using System;

namespace Procure.Models
{
    /// <summary>What an item was last bought for: the newest priced PO line with the same item name,
    /// net of its per-unit discount. Fills a new quote line's Last price.</summary>
    public sealed record LastPaidPrice(decimal Price, string Currency, DateTime? PoDate, string PoNo, string Vendor)
    {
        /// <summary>"PO 4500123 · Dell · 12 Aug 2026" - where the price came from.</summary>
        public string Source =>
            string.Join("  ·  ", new[]
            {
                PoNo.Length == 0 ? "PO"
                    : PoNo.StartsWith("PO", StringComparison.OrdinalIgnoreCase) ? PoNo   // "PO-2914-1", not "PO PO-2914-1"
                    : "PO " + PoNo,
                Vendor,
                PoDate?.ToString("d MMM yyyy") ?? string.Empty,
            }.Where(p => p.Length > 0));
    }
}
