using System;
using System.Globalization;
using System.Linq;
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
            var text = StripCurrency(raw, out var detected);
            return (ParseAmount(text), detected);
        }

        /// <summary>Reads a price typed into a box, and says whether it could be read. Exactly one number
        /// (with an optional currency) is a price; an empty box is "no price". Anything else - "12 - 15",
        /// "86.75 x 2", "abc" - is not guessed at: it returns false and leaves the amount and currency
        /// alone, so the caller can keep what it had and tell the user, instead of storing a wrong number
        /// (86.752) or silently wiping the price.</summary>
        public static bool TryParse(string? raw, string fallbackCurrency, out decimal? amount, out string currency)
        {
            var fallback = string.IsNullOrWhiteSpace(fallbackCurrency) ? "AED" : fallbackCurrency.Trim().ToUpperInvariant();
            amount = null;
            currency = fallback;
            if (string.IsNullOrWhiteSpace(raw)) return true;

            var rest = StripCurrency(raw, out var detected);
            // "1 234,50": a space between thousands is part of one number, not the start of a second.
            rest = Regex.Replace(rest, @"(?<=\d)[ \u00A0](?=\d{3}(?!\d))", "");

            var tokens = Regex.Matches(rest, @"-?\d[\d.,]*");
            if (tokens.Count == 0)
            {
                // Only a currency ("usd"): the currency changes, there is no price yet. Anything else is noise.
                if (detected is null) return false;
                currency = detected;
                return true;
            }
            if (tokens.Count > 1) return false;

            var parsed = ParseAmount(tokens[0].Value);
            if (parsed is null) return false;
            amount = parsed;
            currency = detected ?? fallback;
            return true;
        }

        /// <summary>The text with its currency (a symbol, or a supported 3-letter code) taken out.</summary>
        private static string StripCurrency(string raw, out string? detected)
        {
            var text = raw;
            detected = null;

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

            return text;
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
                // Both present: the one that comes last is the decimal point and the other groups thousands,
                // so 1,234.56 and 1.234,56 are the same number. Only the first used to be understood; the
                // second came out as 1.23456.
                if (sanitized.LastIndexOf(',') > sanitized.LastIndexOf('.'))
                    sanitized = sanitized.Replace(".", "").Replace(",", ".");
                else
                    sanitized = sanitized.Replace(",", "");
            }
            else if (sanitized.Count(c => c == '.') > 1 && Regex.IsMatch(sanitized, @"^-?\d{1,3}(?:\.\d{3})+$"))
            {
                sanitized = sanitized.Replace(".", "");   // 1.234.567: dots grouping thousands
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

            // both separators: the last one is the decimal point
            Check(ParseAmount("1,234.56") == 1234.56m, "english grouping");
            Check(ParseAmount("1.234,56") == 1234.56m, "european grouping");
            Check(Parse("1.234,56 EUR", "AED") is { Amount: 1234.56m, Currency: "EUR" }, "european price with a code");
            Check(ParseAmount("1.234.567") == 1234567m, "dots grouping thousands");
            Check(ParseAmount("12,5") == 12.5m && ParseAmount("1,234") == 1234m && ParseAmount("86.75") == 86.75m, "single separators are unchanged");

            // what can and cannot be read from a box
            bool Try(string text, out decimal? amount, out string cur) => TryParse(text, "USD", out amount, out cur);
            Check(Try("86.75 usd", out var a1, out var c1) && a1 == 86.75m && c1 == "USD", "plain price");
            Check(Try("90", out var a2, out var c2) && a2 == 90m && c2 == "USD", "a bare number keeps the fallback currency");
            Check(Try("1 234,50 eur", out var a3, out var c3) && a3 == 1234.50m && c3 == "EUR", "a space between thousands");
            Check(Try("USD 86.75/pc", out var a4, out _) && a4 == 86.75m, "a unit after the number is ignored");
            Check(Try("", out var a5, out var c5) && a5 is null && c5 == "USD", "an empty box is no price");
            Check(Try("usd", out var a6, out var c6) && a6 is null && c6 == "USD", "a currency alone is accepted");
            Check(Try("eur", out var a7, out var c7) && a7 is null && c7 == "EUR", "and changes the currency");
            Check(!Try("12 - 15", out _, out _), "a range is not a price");
            Check(!Try("12-15", out _, out _), "nor is 12-15");
            Check(!Try("86.75 x 2", out _, out _), "nor is a multiplication (it used to become 86.752)");
            Check(!Try("abc", out _, out _), "nor is plain text");
            Check(!Try("12 5", out _, out _), "nor two separate numbers");
        }
    }
}
