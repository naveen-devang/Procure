using System;
using System.Collections.Generic;

namespace Procure.Services.Export;

internal static class PcrCurrencyConversionSelfCheck
{
    public static void Run()
    {
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = 3.6725m
        };
        var on = new PcrCurrencyConversionOptions(true, "AED", rates);
        var off = new PcrCurrencyConversionOptions(false, "AED", rates);

        Assert(on.TryConvert("USD", 100m, out var converted) && converted == 367.2500m,
            "USD converts into AED using the configured rate");
        Assert(!on.TryConvert("AED", 100m, out _),
            "the local currency is not rendered as a conversion");
        Assert(!off.TryConvert("USD", 100m, out _),
            "the opt-in switch is off by default and disables conversion");
        Assert(PcrCurrencyConversion.RowNeedsTwoLines(on, new string?[] { "AED", "USD" }),
            "a mixed row reserves two lines when one cell converts");
        Assert(!PcrCurrencyConversion.RowNeedsTwoLines(on, new string?[] { "AED", "AED" }),
            "an all-local row stays one line");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("PCR CURRENCY CONVERSION: " + message);
    }
}
