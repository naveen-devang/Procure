using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Procure.Utilities
{
    /// <summary>Months counted from year 0: consecutive months are consecutive integers.</summary>
    public static class MonthIndex
    {
        public static int Of(DateTime d) => d.Year * 12 + d.Month - 1;
        public static DateTime Start(int month) => new(month / 12, month % 12 + 1, 1);
        public static string Short(int month) => Start(month).ToString("MMM", CultureInfo.CurrentCulture);
        public static string Long(int month) => Start(month).ToString("MMM yyyy", CultureInfo.CurrentCulture);
        /// <summary>"yyyy-MM", how ItemNote stores months.</summary>
        public static string Key(int month) => Start(month).ToString("yyyy-MM", CultureInfo.InvariantCulture);

        public static int? Parse(string? key) =>
            DateTime.TryParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? Of(d) : null;
    }

    /// <summary>
    /// The market price of one item, month by month: the middle of that month's quotes, from every
    /// supplier, in the local currency. A month with fewer than <see cref="PriceAnalysis.MinQuotes"/>
    /// quotes has no market price - one quote is a supplier's price, not the market's.
    /// </summary>
    public sealed class MarketSeries
    {
        public static readonly MarketSeries Empty = new(0, Array.Empty<double>());

        public int FirstMonth { get; }
        private readonly double[] _median;   // NaN where there were too few quotes

        public MarketSeries(int firstMonth, double[] median)
        {
            FirstMonth = firstMonth;
            _median = median;
        }

        public int LastMonth => FirstMonth + _median.Length - 1;
        public bool IsEmpty => !_median.Any(v => !double.IsNaN(v));

        public double? InMonth(int month) =>
            month >= FirstMonth && month <= LastMonth && !double.IsNaN(_median[month - FirstMonth]) ? _median[month - FirstMonth] : null;

        /// <summary>The market when <paramref name="month"/> was quoted: that month, else the nearest
        /// month with one no more than two months away. Null when nobody else was quoting then.</summary>
        public double? At(int month)
        {
            for (var d = 0; d <= 2; d++)
            {
                if (InMonth(month - d) is { } before) return before;
                if (InMonth(month + d) is { } after) return after;
            }
            return null;
        }

        /// <summary>The latest month that has a market price.</summary>
        public (int Month, double Value)? Latest()
        {
            for (var m = LastMonth; m >= FirstMonth; m--)
                if (InMonth(m) is { } v) return (m, v);
            return null;
        }

        public IEnumerable<(int Month, double Value)> Points(int from, int to)
        {
            for (var m = Math.Max(from, FirstMonth); m <= Math.Min(to, LastMonth); m++)
                if (InMonth(m) is { } v) yield return (m, v);
        }
    }

    /// <summary>A stretch of months the market ran well above its usual level.</summary>
    public sealed record PriceSpike(int FromMonth, int ToMonth, double Rise);

    public static class PriceAnalysis
    {
        public const int MinQuotes = 3;
        /// <summary>A month is a spike when its market is this far above the period's usual level.</summary>
        public const double SpikeFactor = 1.15;
        /// <summary>A quote older than this is "old": still shown, ranked after recent ones.</summary>
        public const int OldAfterMonths = 6;

        public static double Median(List<double> values)
        {
            values.Sort();
            var n = values.Count;
            return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
        }

        /// <summary>Builds the monthly market from (month, price) quotes, in any order.</summary>
        public static MarketSeries Market(IEnumerable<(int Month, double Price)> quotes)
        {
            var byMonth = new Dictionary<int, List<double>>();
            foreach (var (month, price) in quotes)
            {
                if (!byMonth.TryGetValue(month, out var list)) byMonth[month] = list = new List<double>();
                list.Add(price);
            }
            if (byMonth.Count == 0) return MarketSeries.Empty;

            var first = byMonth.Keys.Min();
            var values = new double[byMonth.Keys.Max() - first + 1];
            Array.Fill(values, double.NaN);
            foreach (var (month, list) in byMonth)
                if (list.Count >= MinQuotes) values[month - first] = Median(list);
            return new MarketSeries(first, values);
        }

        /// <summary>Runs of months between <paramref name="from"/> and <paramref name="to"/> whose market
        /// is at least <see cref="SpikeFactor"/> times the period's usual level (the middle of its
        /// monthly prices). Rise is the peak against that level.</summary>
        public static List<PriceSpike> Spikes(MarketSeries market, int from, int to)
        {
            var spikes = new List<PriceSpike>();
            var points = market.Points(from, to).ToList();
            if (points.Count < 4) return spikes;   // too little history to say what "usual" is

            var usual = Median(points.Select(p => p.Value).ToList());
            int? start = null;
            int last = 0;
            double peak = 0;
            foreach (var (month, value) in points)
            {
                var high = value >= usual * SpikeFactor;
                // A gap month (no market) does not end a spike; a normal month does.
                if (high)
                {
                    start ??= month;
                    last = month;
                    peak = Math.Max(peak, value);
                }
                else if (start is { } s)
                {
                    spikes.Add(new PriceSpike(s, last, peak / usual - 1));
                    start = null;
                    peak = 0;
                }
            }
            if (start is { } open) spikes.Add(new PriceSpike(open, last, peak / usual - 1));
            return spikes;
        }

        /// <summary>"4% above", "5% below", "Level" - a price against the market it was quoted into.</summary>
        public static string VsText(double price, double? market)
        {
            if (market is not { } m || m <= 0) return "No market then";
            var pct = (int)Math.Round((price / m - 1) * 100);
            return pct == 0 ? "Level" : pct > 0 ? $"{pct}% above" : $"{-pct}% below";
        }

        /// <summary>-1 when a price is 3% or more below the market it was quoted into, 1 when 10% or more
        /// above, else 0. Drives the green / red in the tables.</summary>
        public static int VsTone(double price, double? market) =>
            market is not { } m || m <= 0 ? 0 : price / m <= 0.97 ? -1 : price / m >= 1.10 ? 1 : 0;

        /// <summary>Ranking weight for <see cref="VsText"/>: the ratio, or a neutral 1 with no market.</summary>
        public static double VsRatio(double price, double? market) => market is { } m && m > 0 ? price / m : 1;

        /// <summary>"AED 1.28M", "AED 128k", "AED 412.00" - for tiles and table cells.</summary>
        public static string Money(string currency, double amount)
        {
            var a = Math.Abs(amount);
            var text = a >= 1_000_000 ? (amount / 1_000_000).ToString("0.##", CultureInfo.CurrentCulture) + "M"
                : a >= 10_000 ? (amount / 1_000).ToString("0", CultureInfo.CurrentCulture) + "k"
                : amount.ToString(a >= 1_000 ? "N0" : "N2", CultureInfo.CurrentCulture);
            return currency + " " + text;
        }

        public static void SelfCheck()
        {
            static void Check(bool ok, string what)
            {
                if (!ok) throw new InvalidOperationException("PriceAnalysis: " + what);
            }

            Check(Median(new List<double> { 3, 1, 2 }) == 2, "median of odd count");
            Check(Median(new List<double> { 4, 1, 2, 3 }) == 2.5, "median of even count");

            var jan = MonthIndex.Of(new DateTime(2026, 1, 15));
            Check(MonthIndex.Start(jan) == new DateTime(2026, 1, 1), "month index round-trips");
            Check(MonthIndex.Parse(MonthIndex.Key(jan)) == jan, "month key round-trips");

            // Two quotes in a month are not a market; three are, and one wild quote does not move it.
            var market = PriceAnalysis.Market(new[]
            {
                (jan, 100.0), (jan, 102.0),
                (jan + 1, 100.0), (jan + 1, 104.0), (jan + 1, 900.0),
            });
            Check(market.InMonth(jan) is null, "two quotes are not a market");
            Check(market.InMonth(jan + 1) == 104, "median ignores the outlier");
            Check(market.At(jan) == 104, "nearest month stands in");
            Check(market.At(jan + 4) is null, "nothing more than two months away");
            Check(market.Latest() == (jan + 1, 104), "latest market month");

            // Twelve months at 100, a three-month spike to 125, then back.
            var year = new List<(int, double)>();
            for (var m = 0; m < 12; m++)
                for (var q = 0; q < 3; q++)
                    year.Add((jan + m, m is >= 4 and <= 6 ? 125 : 100));
            var ym = PriceAnalysis.Market(year);
            var spikes = Spikes(ym, jan, jan + 11);
            Check(spikes.Count == 1 && spikes[0].FromMonth == jan + 4 && spikes[0].ToMonth == jan + 6, "one spike, May to Jul");
            Check(Math.Abs(spikes[0].Rise - 0.25) < 1e-9, "spike rise against the usual level");
            Check(Spikes(ym, jan, jan + 2).Count == 0, "too little history is never a spike");

            Check(VsText(104, 100) == "4% above" && VsText(95, 100) == "5% below" && VsText(100.2, 100) == "Level", "vs text");
            Check(VsText(100, null) == "No market then", "vs text without a market");

            Check(Money("AED", 1_280_000) == "AED " + 1.28.ToString("0.##", CultureInfo.CurrentCulture) + "M", "millions");
            Check(Money("AED", 128_400) == "AED 128k", "thousands");
        }
    }
}
