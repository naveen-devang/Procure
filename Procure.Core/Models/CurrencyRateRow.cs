namespace Procure.Models;

public sealed class CurrencyRateRow
{
    public string Currency { get; set; } = string.Empty;
    public string RateText { get; set; } = string.Empty;
    public bool IsCustom { get; set; }
}
