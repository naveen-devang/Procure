namespace Procure.App;

/// <summary>x:Bind format helpers for the card template.</summary>
public static class Fmt
{
    public static string Counts(int rfq, int po, int items)
        => $"RFQ {rfq}  ·  PO {po}  ·  items {items}";
}
