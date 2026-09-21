using System;
using System.Collections.Generic;
using System.Linq;

namespace Procure.Services.Export;

public sealed class PcrCurrencyConversionOptions
{
    public static PcrCurrencyConversionOptions Disabled { get; } = new(false, "AED", null);

    private static readonly IReadOnlyDictionary<string, decimal> DefaultAedRates =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["AED"] = 1m,
            ["USD"] = 3.6725m,
            ["EUR"] = 4.218135220192789m,
            ["GBP"] = 4.918560297428186m,
            ["SAR"] = 0.9793333333333333m,
            ["QAR"] = 1.0089285714285714m,
            ["OMR"] = 9.54369909973609m,
            ["KWD"] = 11.912623089647477m,
            ["BHD"] = 9.767287234042552m,
            ["INR"] = 0.03823946249339288m,
            ["SGD"] = 2.8783138356102413m,
            ["CAD"] = 2.6263930434043594m,
            ["AUD"] = 2.616861551228928m,
            ["JPY"] = 0.02340579376776456m,
            ["CNY"] = 0.5483274544140043m
        };

    public bool ShowLocalEquivalent { get; }
    public string LocalCurrency { get; }
    public IReadOnlyDictionary<string, decimal> Rates { get; }

    public static IReadOnlyDictionary<string, decimal> DefaultRatesFor(string? localCurrency)
    {
        var local = Normalize(localCurrency);
        var localAedRate = DefaultAedRates.TryGetValue(local, out var rate) ? rate : 1m;
        return DefaultAedRates.ToDictionary(x => x.Key, x => x.Value / localAedRate,
            StringComparer.OrdinalIgnoreCase);
    }

    public PcrCurrencyConversionOptions(
        bool showLocalEquivalent,
        string? localCurrency,
        IReadOnlyDictionary<string, decimal>? rates)
    {
        ShowLocalEquivalent = showLocalEquivalent;
        LocalCurrency = Normalize(localCurrency);
        Rates = rates ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    }

    public bool HasConversion(string? sourceCurrency)
    {
        var source = Normalize(sourceCurrency);
        return ShowLocalEquivalent
            && !string.Equals(source, LocalCurrency, StringComparison.OrdinalIgnoreCase)
            && Rates.TryGetValue(source, out var rate)
            && rate > 0m;
    }

    public bool TryConvert(string? sourceCurrency, decimal amount, out decimal localAmount)
    {
        localAmount = 0m;
        if (!HasConversion(sourceCurrency)) return false;

        localAmount = amount * Rates[Normalize(sourceCurrency)];
        return true;
    }

    public static string Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "AED" : currency.Trim().ToUpperInvariant();
}

public static class PcrCurrencyConversion
{
    public static bool RowNeedsTwoLines(
        PcrCurrencyConversionOptions options,
        IEnumerable<string?> sourceCurrencies) =>
        options.ShowLocalEquivalent && sourceCurrencies.Any(options.HasConversion);
}
