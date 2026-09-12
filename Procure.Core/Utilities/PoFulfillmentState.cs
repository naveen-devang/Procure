using System;

namespace Procure.Utilities
{
    /// <summary>Which of four states a fulfilment badge's own text describes.
    ///
    /// The classification is pure text, so it belongs with the logic rather than with either head's
    /// colour converters - both of them ask this the same question and only differ in what colour
    /// they paint the answer. It also means PrLineMatcherSelfCheck can pin it without a UI: add a
    /// new badge wording, forget to teach this about it, and the badge silently renders in the
    /// neutral grey that means "nothing to see here" - which on an over-ordered line is the opposite
    /// of the truth.</summary>
    public static class PoFulfillmentState
    {
        public const int Over = 0, Pending = 1, Complete = 2, Neutral = 3;

        public static int Classify(object? value)
        {
            var text = value as string ?? string.Empty;

            if (text.Contains("Exceeds", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Over-allocated", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Over-ordered", StringComparison.OrdinalIgnoreCase))
                return Over;

            // "Missing" and "Qty changed" are the coverage warnings: same amber as a pending
            // quantity, since both mean "this quote does not yet match the requisition".
            if (text.Contains("Pending", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Partial", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Unordered", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Missing", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Qty changed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Not on the requisition", StringComparison.OrdinalIgnoreCase))
                return Pending;

            if (text.Contains("Complete", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Fully Allocated", StringComparison.OrdinalIgnoreCase))
                return Complete;

            return Neutral;
        }
    }
}
