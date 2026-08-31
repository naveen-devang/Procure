using System;
using System.Globalization;

namespace Procure.Utilities
{
    public static class DiscountInput
    {
        // "5" -> 5 (absolute currency amount). "5%" -> 5% of percentBasis, resolved once, here
        // (a snapshot - it does not re-scale if the basis changes later; matches the paste path).
        // Returns null only when the text is not a parseable number.
        public static decimal? Resolve(string? raw, decimal percentBasis)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var isPercent = raw.Contains('%');
            var clean = raw.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();

            if (!decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) &&
                !decimal.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out n))
            {
                return null;
            }

            return isPercent ? Math.Round(percentBasis * n / 100m, 2) : n;
        }
    }
}
