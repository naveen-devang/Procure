namespace Procure.Models
{
    /// <summary>
    /// One collapsed material row on the Raw &amp; Packing tab, aggregated in SQL rather than by
    /// summing lines held in memory. This is what the tab loads; the lines behind a material arrive
    /// only when its group is expanded.
    /// </summary>
    public sealed record MaterialGroupSummary(
        string MaterialName,
        int LineCount,
        decimal TotalOrdered,
        decimal TotalCalledOff,
        string Unit);
}
