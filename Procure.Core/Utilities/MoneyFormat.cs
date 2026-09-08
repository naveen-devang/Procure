using System;

namespace Procure.Utilities
{
    public static class MoneyFormat
    {
        // Shared money renderer: whole amounts stay compact (N0) while fractional amounts keep
        // their cents (N2) instead of being silently rounded away in one-off N0 format strings.
        public static string Format(string? currency, decimal amount)
        {
            var cur = string.IsNullOrWhiteSpace(currency) ? "AED" : currency;
            return amount % 1 == 0 ? $"{cur} {amount:N0}" : $"{cur} {amount:N2}";
        }
    }
}
