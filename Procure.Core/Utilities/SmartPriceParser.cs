using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Procure.Utilities
{
    /// <summary>Parses a price typed as free text - "86.75", "86.75 usd", "$86.75", "USD 86.75" -
    /// into an amount and the currency it names, so a historical/estimated price box can carry its
    /// own currency instead of silently inheriting whatever the surrounding RFQ happens to use.
    /// A currency named in the text always wins; when none is typed, the caller's own fallback
    /// (usually whatever currency that field already held) is kept.</summary>
    public static class SmartPriceParser
    {
        private static readonly (string Symbol, string Currency)[] SymbolCurrencies =
        {
            ("$", "USD"), ("€", "EUR"), ("£", "GBP"), ("₹", "INR"),
        };

        public static (decimal? Amount, string Currency) Parse(string? raw, string fallbackCurrency)
        {
            var fallback = string.IsNullOrWhiteSpace(fallbackCurrency) ? "AED" : fallbackCurrency.Trim().ToUpperInvariant();
            var (amount, detected) = ParseWithDetectedCurrency(raw);
            return (amount, detected ?? fallback);
        }

        /// <summary>Same parse, but the currency comes back null rather than defaulted when none was
        /// typed - what a caller with its own "leave it alone" fallback (e.g. a pasted cell that
        /// shouldn't stomp the row's existing currency) needs instead of <see cref="Parse"/>.</summary>
        public static (decimal? Amount, string? Currency) ParseWithDetectedCurrency(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);

            var text = raw;
            string? detected = null;

            foreach (var (symbol, currency) in SymbolCurrencies)
            {
                if (text.Contains(symbol, StringComparison.Ordinal))
                {
                    detected = currency;
                    text = text.Replace(symbol, "");
                    break;
                }
            }

            if (detected == null)
            {
                // A bare 3-letter word - "usd", "AED" - is a currency code, not a unit or a typo,
                // as long as it's one this app actually supports (AppConstants.SupportedCurrencies).
                foreach (Match m in Regex.Matches(text, @"[A-Za-z]{3}"))
                {
                    var code = m.Value.ToUpperInvariant();
                    if (Array.IndexOf(AppConstants.SupportedCurrencies, code) >= 0)
                    {
                        detected = code;
                        text = text.Remove(m.Index, m.Length);
                        break;
                    }
                }
            }

            return (ParseAmount(text), detected);
        }

        /// <summary>The bare-number half of price parsing, shared with clipboard paste
        /// (<see cref="ClipboardItemParser"/>): strips symbols/formatting and resolves comma vs.
        /// dot as thousands separator vs. decimal point.</summary>
        public static decimal? ParseAmount(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var sanitized = Regex.Replace(raw, @"[^\d\.\,\-]", "").Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
                return null;

            if (sanitized.Contains(',') && !sanitized.Contains('.'))
            {
                if (Regex.IsMatch(sanitized, @"^-?\d{1,3}(?:,\d{3})+$"))
                {
                    sanitized = sanitized.Replace(",", "");
                }
                else if (Regex.IsMatch(sanitized, @"^-?\d+,\d{1,2}$"))
                {
                    sanitized = sanitized.Replace(",", ".");
                }
                else
                {
                    sanitized = sanitized.Replace(",", "");
                }
            }
            else if (sanitized.Contains(',') && sanitized.Contains('.'))
            {
                sanitized = sanitized.Replace(",", "");
            }

            return decimal.TryParse(sanitized, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
        }

        /// <summary>Self-check: <c>SmartPriceParser.Demo()</c> asserts the parser's core claims -
        /// currency detection, symbol mapping, fallback - fail loudly instead of silently.</summary>
        public static void Demo()
        {
            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException($"SmartPriceParser self-check failed: {message}");
            }

            var a = Parse("86.75 usd", "AED");
            Check(a.Amount == 86.75m && a.Currency == "USD", "trailing lowercase code");

            var b = Parse("USD 86.75", "AED");
            Check(b.Amount == 86.75m && b.Currency == "USD", "leading uppercase code");

            var c = Parse("$86.75", "AED");
            Check(c.Amount == 86.75m && c.Currency == "USD", "dollar symbol");

            var d = Parse("86.75", "USD");
            Check(d.Amount == 86.75m && d.Currency == "USD", "no currency typed keeps the fallback");

            var e = Parse("1,250.50 EUR", "AED");
            Check(e.Amount == 1250.50m && e.Currency == "EUR", "thousands separator plus code");

            var f = Parse("", "AED");
            Check(f.Amount == null && f.Currency == "AED", "empty text");
        }
    }
}
