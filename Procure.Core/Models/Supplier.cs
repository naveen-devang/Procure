using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Procure.Utilities;

namespace Procure.Models
{
    /// <summary>What was spent with a vendor in one currency.</summary>
    public sealed record SpendLine(string Currency, decimal Total);

    /// <summary>"AED 120,000  +  USD 8,400" - currencies are never added together.</summary>
    public static class SpendFormat
    {
        public static string Format(IReadOnlyList<SpendLine> spend) =>
            spend.Count == 0 ? "No orders yet"
            : string.Join("  +  ", spend.OrderByDescending(s => s.Total).Select(s => MoneyFormat.Format(s.Currency, s.Total)));
    }

    /// <summary>The tag a user puts on a supplier. Stored as the text itself.</summary>
    public static class SupplierTag
    {
        public const string None = "";
        public const string Preferred = "Preferred";
        public const string Avoid = "Avoid";
    }

    /// <summary>One row of the Suppliers list.</summary>
    public sealed partial class SupplierListItem : ObservableModel
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Tag { get; init; } = string.Empty;
        public DateTime? LastUsed { get; init; }
        public IReadOnlyList<SpendLine> Spend { get; init; } = Array.Empty<SpendLine>();

        public string SpendText => SpendFormat.Format(Spend);
        public string LastUsedText => LastUsed is { } d ? "Last used " + d.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : string.Empty;
        public bool IsPreferred => Tag == SupplierTag.Preferred;
        public bool IsAvoid => Tag == SupplierTag.Avoid;
        public string SubLine => LastUsed is { } d
            ? $"{SpendText} · last used {d.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}"
            : SpendText;

        [ObservableProperty]
        public partial bool IsSelected { get; set; }
    }

    /// <summary>The top of a supplier's page: who they are and four figures, all over the chosen period.</summary>
    public sealed class SupplierSummary
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Tag { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string Person { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;
        public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

        /// <summary>Rounds (same PR, same item) where someone else also priced it, and how many they were cheapest in.</summary>
        public int Rounds { get; init; }
        public int RoundsWon { get; init; }
        public string BoughtText { get; init; } = string.Empty;
        public int PoCount { get; init; }

        public bool IsPreferred => Tag == SupplierTag.Preferred;
        public bool IsAvoid => Tag == SupplierTag.Avoid;

        public string ContactLine
        {
            get
            {
                var parts = new[] { Email, Person, Phone }.Where(p => p.Length > 0).ToList();
                return parts.Count == 0 ? "No contact details yet" : string.Join("  ·  ", parts);
            }
        }

        public string HeadToHeadText => Rounds == 0 ? "No shared rounds" : $"Cheapest in {RoundsWon} of {Rounds}";
        public string PoCountText => PoCount == 1 ? "1 PO" : $"{PoCount:N0} POs";
        public string BoughtLine => $"{BoughtText}  ·  {PoCountText}";
    }

    /// <summary>One item a supplier quoted or sold, as a row of their items list. The chart and the
    /// other suppliers' prices are read only when the row is opened.</summary>
    public sealed partial class VendorItemRow : ObservableModel
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Chevron))]
        public partial bool IsExpanded { get; set; }

        [ObservableProperty]
        public partial PriceChartData? Chart { get; set; }

        [ObservableProperty]
        public partial IReadOnlyList<OtherQuote> Others { get; set; } = Array.Empty<OtherQuote>();

        public string Chevron => IsExpanded ? "▾" : "▸";
    }

    /// <summary>Another supplier's latest price for the same item.</summary>
    public sealed record OtherQuote(string Vendor, string PriceText, bool IsOld)
    {
        /// <summary>The cheapest recent one, shown in green.</summary>
        public bool IsBest { get; init; }
    }

    /// <summary>One row of the Items list.</summary>
    public sealed partial class ItemListItem : ObservableModel
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        /// <summary>A differently written item that is probably the same one ("GASKET 4 CORE").</summary>
        public string TwinKey { get; init; } = string.Empty;
        public string TwinName { get; init; } = string.Empty;

        public bool HasTwin => TwinKey.Length > 0;
        public string TwinText => $"Looks like the same item as \"{TwinName}\"";

        [ObservableProperty]
        public partial bool IsSelected { get; set; }
    }

    /// <summary>One supplier on an item's page, in "who to ask next time" order.</summary>
    public sealed class ItemSupplierRank
    {
        public int Rank { get; set; }
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Tag { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string LatestText { get; init; } = "-";
        public string VsText { get; init; } = "-";
        public string RepliesText { get; init; } = "-";
        public string SaidNoText { get; init; } = "-";
        public string BoughtText { get; init; } = "-";
        public bool IsOld { get; init; }

        public int VsTone { get; init; }

        public string RankText => Rank.ToString(CultureInfo.CurrentCulture);
        /// <summary>The top three recent, non-Avoid suppliers are set in bold, as in the design.</summary>
        public bool IsTop => Rank <= 3 && !IsOld && !IsAvoid;
        /// <summary>Old prices and Avoid suppliers are faded: still worth knowing, not first to ask.</summary>
        public bool IsFaded => IsOld || IsAvoid;
        public bool IsPreferred => Tag == SupplierTag.Preferred;
        public bool IsAvoid => Tag == SupplierTag.Avoid;
    }

    /// <summary>A note on a stretch of months for one item.</summary>
    public sealed record ItemNoteRow(string Id, int FromMonth, int ToMonth, string Text)
    {
        public string Period => FromMonth == ToMonth ? MonthIndex.Long(FromMonth) : $"{MonthIndex.Long(FromMonth)} – {MonthIndex.Long(ToMonth)}";
    }

    /// <summary>An item's page.</summary>
    public sealed class ItemDetail
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string HeaderText { get; init; } = string.Empty;
        public PriceChartData Chart { get; init; } = PriceChartData.Empty;
        /// <summary>What buying inside a spike cost, against today's market. Empty when nothing was.</summary>
        public string SpikeCostText { get; init; } = string.Empty;
        public IReadOnlyList<ItemSupplierRank> Suppliers { get; init; } = Array.Empty<ItemSupplierRank>();
        public IReadOnlyList<ItemNoteRow> Notes { get; init; } = Array.Empty<ItemNoteRow>();
        /// <summary>Other spellings folded into this item with "Treat as one item".</summary>
        public IReadOnlyList<(string Key, string Name)> Aliases { get; init; } = Array.Empty<(string, string)>();

        public bool HasAliases => Aliases.Count > 0;
        public string AliasText => "Also counted here: " + string.Join(", ", Aliases.Select(a => "\"" + a.Name + "\""));
        public bool HasSpikeCost => SpikeCostText.Length > 0;
    }

    /// <summary>A price point on a chart. X is a fractional month index.</summary>
    public sealed record ChartDot(double X, double Price, bool IsPo, string Vendor, string When, string Ref);

    /// <summary>A shaded stretch of months: a spike, a note, or both.</summary>
    public sealed record ChartBand(int FromMonth, int ToMonth, string Title, string Note, bool IsSpike);

    /// <summary>Everything a price chart draws, already in the local currency.</summary>
    public sealed class PriceChartData
    {
        public static readonly PriceChartData Empty = new();

        public int FromMonth { get; init; }
        public int ToMonth { get; init; }
        public string Currency { get; init; } = "AED";
        public string Unit { get; init; } = "pc";
        public IReadOnlyList<(int Month, double Value)> Market { get; init; } = Array.Empty<(int, double)>();
        public IReadOnlyList<ChartDot> Dots { get; init; } = Array.Empty<ChartDot>();
        public IReadOnlyList<ChartBand> Bands { get; init; } = Array.Empty<ChartBand>();
        /// <summary>Legend words for the dots: "Bought from Al Noor" / "Al Noor quote", or the item view's.</summary>
        public string PoLegend { get; init; } = "Bought (PO)";
        public string QuoteLegend { get; init; } = "Quote";
        public string MarketLegend { get; init; } = "Market";
        /// <summary>The item view rings every supplier's quotes in grey; one supplier's own are in blue.</summary>
        public bool GreyQuotes { get; init; }
        /// <summary>The item view's "Point at a dot for the supplier and unit price".</summary>
        public bool ShowHint { get; init; }

        public bool IsEmpty => Market.Count == 0 && Dots.Count == 0;
    }
}
