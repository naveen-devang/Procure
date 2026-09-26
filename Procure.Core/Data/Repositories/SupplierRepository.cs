using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;
using Procure.Utilities;

namespace Procure.Data.Repositories
{
    public sealed class SupplierRepository : ISupplierRepository
    {
        private readonly SqliteDatabase _db;

        public SupplierRepository(SqliteDatabase db)
        {
            _db = db;
            // A DI singleton. Any RFQ or PO write can move a market price, so the cache starts over.
            DataChangeNotifier.Changed += _ => ClearMarkets();
        }

        // ---- SQL pieces -----------------------------------------------------------------------

        private const string Priced = "ri.IsQuoted = 1 AND ri.QuotedUnitPrice > 0";
        private const string NetQuote = "max(0, ri.QuotedUnitPrice - COALESCE(ri.Discount, 0))";
        private const string NetPoLine = "max(0, pi.UnitPrice - COALESCE(pi.Discount, 0))";
        private const string RfqCurrency = "COALESCE(NULLIF(trim(r.Currency), ''), 'AED')";
        private const string PoCurrency = "COALESCE(NULLIF(trim(p.Currency), ''), 'AED')";

        /// <summary>The item a line counts towards: its own name, or the one it was folded into.</summary>
        private static string Canonical(string name) =>
            $"COALESCE((SELECT CanonicalKey FROM ItemAlias WHERE AliasKey = lower(trim({name}))), lower(trim({name})))";

        // ---- the suppliers list ---------------------------------------------------------------

        public Task<(List<SupplierListItem> Rows, int Total)> GetPageAsync(string search, SupplierSort sort, string tag,
            IReadOnlyDictionary<string, decimal> toLocal, int skip, int take) => Task.Run(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            var text = EscapeLike(search.Trim());
            const string from = "FROM VendorAggregate AS a LEFT JOIN VendorContact AS c ON c.VendorKey = a.VendorKey ";
            const string where = "WHERE (@Text = '' OR a.VendorName LIKE '%' || @Text || '%' ESCAPE '!') AND (@Tag = '' OR COALESCE(c.Tag, '') = @Tag)";

            int total;
            using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) " + from + where + ";";
                count.Parameters.AddWithValue("@Text", text);
                count.Parameters.AddWithValue("@Tag", tag);
                total = Convert.ToInt32(await count.ExecuteScalarAsync().ConfigureAwait(false));
            }

            using var cmd = connection.CreateCommand();
            string order;
            switch (sort)
            {
                case SupplierSort.Name:
                    order = "a.VendorName COLLATE NOCASE";
                    break;
                case SupplierSort.Spend:
                    // Each currency at its rate to the local currency; a currency without a rate
                    // adds nothing rather than being counted as if it were local.
                    var cases = string.Join(" ", toLocal.Select((r, i) =>
                    {
                        cmd.Parameters.AddWithValue("@Cur" + i, r.Key);
                        cmd.Parameters.AddWithValue("@Rate" + i, (double)r.Value);
                        return $"WHEN @Cur{i} THEN @Rate{i}";
                    }));
                    order = "(SELECT SUM(s.Total * CASE s.Currency " + cases + " ELSE 0 END) FROM VendorSpend AS s " +
                            "WHERE s.VendorKey = a.VendorKey) DESC, a.VendorName COLLATE NOCASE";
                    break;
                default:
                    order = "a.LastUsed DESC, a.VendorName COLLATE NOCASE";
                    break;
            }

            cmd.CommandText = "SELECT a.VendorKey, a.VendorName, a.LastUsed, COALESCE(c.Tag, '') " + from + where +
                              " ORDER BY " + order + " LIMIT @Take OFFSET @Skip;";
            cmd.Parameters.AddWithValue("@Text", text);
            cmd.Parameters.AddWithValue("@Tag", tag);
            cmd.Parameters.AddWithValue("@Take", take);
            cmd.Parameters.AddWithValue("@Skip", skip);

            var keys = new List<(string Key, string Name, DateTime? Last, string Tag)>();
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                while (await reader.ReadAsync().ConfigureAwait(false))
                    keys.Add((reader.GetString(0), reader.GetString(1), ParseDate(reader, 2), reader.GetString(3)));

            var spend = await ReadSpendAsync(connection, keys.Select(k => k.Key).ToList()).ConfigureAwait(false);
            var rows = keys.Select(k => new SupplierListItem
            {
                Key = k.Key,
                Name = k.Name,
                Tag = k.Tag,
                LastUsed = k.Last,
                Spend = spend.TryGetValue(k.Key, out var s) ? s : Array.Empty<SpendLine>(),
            }).ToList();
            return (rows, total);
        });

        // ---- one supplier ---------------------------------------------------------------------

        public Task<SupplierSummary?> GetSummaryAsync(string vendorKey, PriceContext ctx) => Task.Run<SupplierSummary?>(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);

            string name;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT VendorName FROM VendorAggregate WHERE VendorKey = @K;";
                cmd.Parameters.AddWithValue("@K", vendorKey);
                if (await cmd.ExecuteScalarAsync().ConfigureAwait(false) is not string n) return null;   // no RFQ or PO names it any more
                name = n;
            }

            string email = "", notes = "", person = "", phone = "", tag = "";
            string? chosen = null;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Email, Notes, Person, Phone, Tag, Categories FROM VendorContact WHERE VendorKey = @K;";
                cmd.Parameters.AddWithValue("@K", vendorKey);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    (email, notes, person, phone, tag) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
                    chosen = reader.IsDBNull(5) ? null : reader.GetString(5);
                }
            }

            // What they sell: the user's own list once they have edited it, otherwise a guess from the
            // words in the item names they were asked about.
            IReadOnlyList<string> categories;
            if (chosen is not null)
                categories = chosen.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            else
            {
                var itemNames = new List<string>();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"SELECT DISTINCT lower(trim(ri.ItemName)) FROM RequestForQuotation AS r
JOIN RfqItem AS ri ON ri.RfqId = r.Id WHERE lower(trim(r.Vendor)) = @K LIMIT 5000;";
                cmd.Parameters.AddWithValue("@K", vendorKey);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) itemNames.Add(reader.GetString(0));
                categories = ItemCategories.Of(itemNames);
            }

            var rounds = await ReadRoundsAsync(connection, vendorKey, ctx).ConfigureAwait(false);

            // Bought in the period, in the local currency; a currency without a rate is shown beside it.
            double local = 0;
            var unconverted = new List<SpendLine>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"SELECT {PoCurrency}, SUM(COALESCE(p.Value, 0)) FROM PurchaseOrder AS p
WHERE lower(trim(p.Vendor)) = @K AND COALESCE(p.Date, '') >= @Since GROUP BY 1;";
                cmd.Parameters.AddWithValue("@K", vendorKey);
                cmd.Parameters.AddWithValue("@Since", ctx.SinceKey);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var total = reader.GetDouble(1);
                    if (ctx.Rate(reader.GetString(0)) is { } rate) local += total * rate;
                    else unconverted.Add(new SpendLine(reader.GetString(0), (decimal)total));
                }
            }
            var poCount = await ScalarAsync(connection, $@"SELECT COUNT(DISTINCT COALESCE(NULLIF(trim(PoNo), ''), Id)) FROM PurchaseOrder
WHERE lower(trim(Vendor)) = @K AND COALESCE(Date, '') >= @Since;", vendorKey, ctx.SinceKey).ConfigureAwait(false);
            var bought = local == 0 && unconverted.Count == 0 ? "Nothing yet"
                : string.Join("  +  ", new[] { local > 0 || unconverted.Count == 0 ? PriceAnalysis.Money(ctx.Local, local) : null }
                    .Concat(unconverted.Select(u => MoneyFormat.Format(u.Currency, u.Total))).Where(s => s is not null));

            return new SupplierSummary
            {
                Key = vendorKey,
                Name = name,
                Tag = tag,
                Email = email,
                Person = person,
                Phone = phone,
                Notes = notes,
                Categories = categories,
                Rounds = rounds.Count,
                RoundsWon = rounds.Won,
                BoughtText = bought,
                PoCount = poCount,
            };
        });

        public Task<List<string>> GetVendorItemKeysAsync(string vendorKey, PriceContext ctx) => Task.Run(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
SELECT k FROM (
    SELECT {Canonical("ri.ItemName")} AS k, ri.QuoteDate AS d
    FROM RequestForQuotation AS r JOIN RfqItem AS ri ON ri.RfqId = r.Id
    WHERE lower(trim(r.Vendor)) = @K AND ri.QuoteDate >= @Since AND trim(ri.ItemName) <> ''
    UNION ALL
    SELECT {Canonical("pi.ItemName")}, pi.PoDate
    FROM PurchaseOrder AS p JOIN PurchaseOrderItem AS pi ON pi.PoId = p.Id
    WHERE lower(trim(p.Vendor)) = @K AND pi.PoDate >= @Since AND trim(pi.ItemName) <> '')
GROUP BY k ORDER BY max(d) DESC, k;";
            cmd.Parameters.AddWithValue("@K", vendorKey);
            cmd.Parameters.AddWithValue("@Since", ctx.SinceKey);
            var keys = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) keys.Add(reader.GetString(0));
            return keys;
        });

        public Task<List<VendorItemRow>> GetVendorItemRowsAsync(IReadOnlyList<string> itemKeys) => Task.Run(async () =>
        {
            if (itemKeys.Count == 0) return new List<VendorItemRow>();
            using var connection = await OpenAsync().ConfigureAwait(false);
            var names = await ItemNamesAsync(connection, itemKeys).ConfigureAwait(false);
            return itemKeys.Select(k => new VendorItemRow { Key = k, Name = names.TryGetValue(k, out var n) ? n : k }).ToList();
        });

        public Task<(PriceChartData Chart, List<OtherQuote> Others)> GetVendorItemDetailAsync(string vendorKey, string itemKey, PriceContext ctx) => Task.Run(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            var raw = await RawKeysAsync(connection, new[] { itemKey }).ConfigureAwait(false);
            var market = (await MarketsAsync(connection, new[] { itemKey }, ctx).ConfigureAwait(false))[itemKey];

            var vendorName = vendorKey;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT VendorName FROM VendorAggregate WHERE VendorKey = @K;";
                cmd.Parameters.AddWithValue("@K", vendorKey);
                if (await cmd.ExecuteScalarAsync().ConfigureAwait(false) is string n) vendorName = n;
            }

            var dots = new List<ChartDot>();
            var unit = string.Empty;
            using (var cmd = connection.CreateCommand())
            {
                var inList = InList(cmd, "@R", raw.Keys);
                cmd.CommandText = $@"
SELECT * FROM (SELECT ri.QuoteDate AS d, {NetQuote}, {RfqCurrency}, 0, COALESCE(pr.PrNo, ''), COALESCE(ri.Unit, '')
    FROM RequestForQuotation AS r JOIN RfqItem AS ri ON ri.RfqId = r.Id LEFT JOIN PurchaseRequisition AS pr ON pr.Id = r.PrId
    WHERE lower(trim(r.Vendor)) = @K AND ri.QuoteDate >= @Since AND {Priced} AND lower(trim(ri.ItemName)) IN ({inList})
    ORDER BY d DESC LIMIT {MaxDots})
UNION ALL
SELECT * FROM (SELECT pi.PoDate AS d, {NetPoLine}, {PoCurrency}, 1, COALESCE(p.PoNo, ''), COALESCE(pi.Unit, '')
    FROM PurchaseOrder AS p JOIN PurchaseOrderItem AS pi ON pi.PoId = p.Id
    WHERE lower(trim(p.Vendor)) = @K AND pi.PoDate >= @Since AND pi.UnitPrice > 0 AND lower(trim(pi.ItemName)) IN ({inList})
    ORDER BY d DESC LIMIT {MaxDots});";
                cmd.Parameters.AddWithValue("@K", vendorKey);
                cmd.Parameters.AddWithValue("@Since", ctx.SinceKey);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var date = reader.GetString(0);
                    if (ctx.Rate(reader.GetString(2)) is not { } rate || X(date) is not { } x) continue;
                    if (unit.Length == 0) unit = reader.GetString(5);
                    dots.Add(new ChartDot(x, reader.GetDouble(1) * rate, reader.GetInt64(3) == 1, vendorName, WhenText(date), reader.GetString(4)));
                }
            }

            // Everyone else's latest price for the item, newest first, one per supplier.
            var others = new List<OtherQuote>();
            var othersPrice = new List<double>();
            using (var cmd = connection.CreateCommand())
            {
                var inList = InList(cmd, "@R", raw.Keys);
                cmd.CommandText = $@"
SELECT lower(trim(r.Vendor)), trim(r.Vendor), ri.QuoteDate, {NetQuote}, {RfqCurrency}
FROM RfqItem AS ri JOIN RequestForQuotation AS r ON r.Id = ri.RfqId
WHERE lower(trim(ri.ItemName)) IN ({inList}) AND {Priced} AND lower(trim(r.Vendor)) <> @K
ORDER BY ri.QuoteDate DESC;";
                cmd.Parameters.AddWithValue("@K", vendorKey);
                var seen = new HashSet<string>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (others.Count < 5 && await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (ctx.Rate(reader.GetString(4)) is not { } rate || !seen.Add(reader.GetString(0))) continue;
                    var month = MonthOf(reader.GetString(2));
                    var old = month is null || ctx.ThisMonth - month > PriceAnalysis.OldAfterMonths;
                    othersPrice.Add(reader.GetDouble(3) * rate);
                    others.Add(new OtherQuote(reader.GetString(1),
                        PriceAnalysis.Money(ctx.Local, reader.GetDouble(3) * rate) + (month is { } m ? " · " + MonthLabel(m, ctx.ThisMonth) : "") + (old ? ", old" : ""),
                        old));
                }
            }

            // The cheapest recent one is the one to beat.
            var best = Enumerable.Range(0, others.Count).Where(i => !others[i].IsOld).OrderBy(i => othersPrice[i]).Select(i => (int?)i).FirstOrDefault();
            if (best is { } b) others[b] = others[b] with { IsBest = true };

            var from = ctx.SinceMonth ?? Math.Min(dots.Count > 0 ? (int)dots.Min(d => d.X) : ctx.ThisMonth,
                market.IsEmpty ? ctx.ThisMonth : market.FirstMonth);
            var notes = await ReadNotesAsync(connection, itemKey).ConfigureAwait(false);
            var chart = new PriceChartData
            {
                FromMonth = from,
                ToMonth = ctx.ThisMonth,
                Currency = ctx.Local,
                Unit = UnitOf(unit),
                Market = market.Points(from, ctx.ThisMonth).ToList(),
                Dots = dots,
                Bands = Bands(market, notes, from, ctx.ThisMonth, itemView: false),
                PoLegend = "Bought from " + ShortName(vendorName),
                QuoteLegend = ShortName(vendorName) + " quote",
            };
            return (chart, others);
        });

        public Task SaveContactAsync(string vendorKey, string email, string person, string phone, string notes, string tag,
            IReadOnlyList<string>? categories) => Task.Run(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            // Categories left null keep whatever is stored (the guess, if never edited).
            cmd.CommandText = @"
INSERT INTO VendorContact (VendorKey, Email, Notes, Person, Phone, Tag, Categories) VALUES (@K, @Email, @Notes, @Person, @Phone, @Tag, @Cats)
ON CONFLICT(VendorKey) DO UPDATE SET Email = excluded.Email, Notes = excluded.Notes, Person = excluded.Person,
    Phone = excluded.Phone, Tag = excluded.Tag, Categories = COALESCE(excluded.Categories, VendorContact.Categories);";
            cmd.Parameters.AddWithValue("@Cats", categories is null ? DBNull.Value
                : string.Join("\n", categories.Select(c => c.Replace('\n', ' ').Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));
            cmd.Parameters.AddWithValue("@K", vendorKey);
            cmd.Parameters.AddWithValue("@Email", email.Trim());
            cmd.Parameters.AddWithValue("@Notes", notes);
            cmd.Parameters.AddWithValue("@Person", person.Trim());
            cmd.Parameters.AddWithValue("@Phone", phone.Trim());
            cmd.Parameters.AddWithValue("@Tag", tag);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        });

        // ---- the items list -------------------------------------------------------------------

        private const string NotAnAlias = "NOT EXISTS (SELECT 1 FROM ItemAlias AS x WHERE x.AliasKey = {0}.ItemKey)";

        public Task<(List<ItemListItem> Rows, int Total)> GetItemPageAsync(string search, int skip, int take) => Task.Run(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            var text = EscapeLike(search.Trim());
            var where = $"WHERE {string.Format(NotAnAlias, "a")} AND (@Text = '' OR a.ItemName LIKE '%' || @Text || '%' ESCAPE '!')";

            int total;
            using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM ItemAggregate AS a " + where + ";";
                count.Parameters.AddWithValue("@Text", text);
                total = Convert.ToInt32(await count.ExecuteScalarAsync().ConfigureAwait(false));
            }

            // A likely duplicate is another item with the same letters and digits once spaces and
            // punctuation are gone. The busier of the two is offered as the one to keep.
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
SELECT k, n, COALESCE(t, ''), COALESCE((SELECT ItemName FROM ItemAggregate WHERE ItemKey = t), '') FROM (
    SELECT a.ItemKey AS k, a.ItemName AS n,
           (SELECT b.ItemKey FROM ItemAggregate AS b
            WHERE b.LooseKey = a.LooseKey AND b.ItemKey <> a.ItemKey AND {string.Format(NotAnAlias, "b")}
              AND (b.LineCount > a.LineCount OR (b.LineCount = a.LineCount AND b.ItemKey < a.ItemKey))
            ORDER BY b.LineCount DESC LIMIT 1) AS t
    FROM ItemAggregate AS a {where}
    ORDER BY a.LineCount DESC, a.ItemName COLLATE NOCASE
    LIMIT @Take OFFSET @Skip);";
            cmd.Parameters.AddWithValue("@Text", text);
            cmd.Parameters.AddWithValue("@Take", take);
            cmd.Parameters.AddWithValue("@Skip", skip);
            var rows = new List<ItemListItem>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                rows.Add(new ItemListItem { Key = reader.GetString(0), Name = reader.GetString(1), TwinKey = reader.GetString(2), TwinName = reader.GetString(3) });
            return (rows, total);
        });

        // ---- one item -------------------------------------------------------------------------

        public Task<ItemDetail?> GetItemDetailAsync(string itemKey, PriceContext ctx) => Task.Run<ItemDetail?>(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            var raw = await RawKeysAsync(connection, new[] { itemKey }).ConfigureAwait(false);
            var names = await ItemNamesAsync(connection, raw.Keys.ToList()).ConfigureAwait(false);
            if (!names.TryGetValue(itemKey, out var name)) return null;   // no line names it any more
            var market = (await MarketsAsync(connection, new[] { itemKey }, ctx).ConfigureAwait(false))[itemKey];
            var since = ctx.SinceKey;

            // One pass over the item's quotes, newest first: each supplier's latest price, how often
            // they said no, how fast they reply, and the dots for the chart.
            // ponytail: reads every quote line of the item once per open - fine to tens of thousands
            // of lines per item; past that, keep per-supplier rows in a trigger-maintained table.
            var vendors = new Dictionary<string, VendorTally>();
            var dots = new List<ChartDot>();
            var quotesSeen = 0;
            var rng = new Random(26);
            var unit = string.Empty;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT lower(trim(r.Vendor)), trim(r.Vendor), ri.QuoteDate, {Priced}, {NetQuote}, {RfqCurrency},
       trim(COALESCE(ri.PriceNote, '')) <> '', COALESCE(pr.PrNo, ''), COALESCE(ri.Unit, ''),
       julianday(substr(r.QuoteReceivedDate, 1, 10)) - julianday(substr(r.SentDate, 1, 10))
FROM RfqItem AS ri JOIN RequestForQuotation AS r ON r.Id = ri.RfqId
LEFT JOIN PurchaseRequisition AS pr ON pr.Id = r.PrId
WHERE lower(trim(ri.ItemName)) IN ({InList(cmd, "@R", raw.Keys)}) AND trim(r.Vendor) <> ''
ORDER BY ri.QuoteDate DESC;";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var key = reader.GetString(0);
                    if (!vendors.TryGetValue(key, out var v)) vendors[key] = v = new VendorTally(reader.GetString(1));
                    var date = reader.GetString(2);
                    var priced = reader.GetInt64(3) == 1;
                    v.Asked++;
                    if (!priced && reader.GetInt64(6) == 1) v.Declined++;
                    if (!reader.IsDBNull(9)) { v.ReplyDays += Math.Max(0, reader.GetDouble(9)); v.Replies++; }
                    if (!priced || ctx.Rate(reader.GetString(5)) is not { } rate) continue;

                    var price = reader.GetDouble(4) * rate;
                    if (v.LatestMonth is null && MonthOf(date) is { } m) (v.LatestMonth, v.LatestPrice) = (m, price);
                    if (unit.Length == 0) unit = reader.GetString(8);
                    if (string.CompareOrdinal(date, since) >= 0 && X(date) is { } x)
                        Sample(dots, ref quotesSeen, rng, () => new ChartDot(x, price, false, v.Name, WhenText(date), reader.GetString(7)));
                }
            }

            // Orders: who it was bought from, and what was bought in the period.
            var orders = new List<(int Month, double Price, double Qty)>();
            var poDots = new List<ChartDot>();
            var ordersSeen = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT lower(trim(p.Vendor)), trim(p.Vendor), pi.PoDate, {NetPoLine}, {PoCurrency}, COALESCE(pi.Quantity, 0), COALESCE(p.PoNo, ''), COALESCE(pi.Unit, '')
FROM PurchaseOrderItem AS pi JOIN PurchaseOrder AS p ON p.Id = pi.PoId
WHERE lower(trim(pi.ItemName)) IN ({InList(cmd, "@R", raw.Keys)}) AND trim(p.Vendor) <> ''
ORDER BY pi.PoDate DESC;";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var key = reader.GetString(0);
                    if (!vendors.TryGetValue(key, out var v)) vendors[key] = v = new VendorTally(reader.GetString(1));
                    v.Bought++;
                    var date = reader.GetString(2);
                    if (string.CompareOrdinal(date, since) < 0) continue;
                    if (unit.Length == 0) unit = reader.GetString(7);
                    var rate = ctx.Rate(reader.GetString(4));
                    var price = reader.GetDouble(3) * (rate ?? 0);
                    if (MonthOf(date) is { } m) orders.Add((m, rate is null ? double.NaN : price, reader.GetDouble(5)));
                    if (rate is not null && price > 0 && X(date) is { } x)
                        Sample(poDots, ref ordersSeen, rng, () => new ChartDot(x, price, true, v.Name, WhenText(date), reader.GetString(6)));
                }
            }

            dots.AddRange(poDots);

            // Tags and emails for everyone who quoted it.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT VendorKey, Tag, Email FROM VendorContact WHERE VendorKey IN ({InList(cmd, "@V", vendors.Keys)});";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                    (vendors[reader.GetString(0)].Tag, vendors[reader.GetString(0)].Email) = (reader.GetString(1), reader.GetString(2));
            }

            // Who to ask next time: Avoid last, then old prices after recent ones, then price against
            // the market it was quoted into, then reply speed, then how often they say no.
            var ranked = vendors
                .Select(kv => (Key: kv.Key, T: kv.Value,
                    Old: kv.Value.LatestMonth is not { } lm || ctx.ThisMonth - lm > PriceAnalysis.OldAfterMonths,
                    Vs: kv.Value.LatestMonth is { } m ? PriceAnalysis.VsRatio(kv.Value.LatestPrice, market.At(m)) : double.MaxValue))
                .Where(v => v.T.LatestMonth is not null || v.T.Bought > 0 || v.T.Declined > 0)
                .OrderBy(v => v.T.Tag == SupplierTag.Avoid)
                .ThenBy(v => v.T.LatestMonth is null)
                .ThenBy(v => v.Old)
                .ThenBy(v => v.Vs)
                .ThenBy(v => v.T.Replies == 0 ? double.MaxValue : v.T.ReplyDays / v.T.Replies)
                .ThenBy(v => v.T.Asked == 0 ? 0 : (double)v.T.Declined / v.T.Asked)
                .Select((v, i) => new ItemSupplierRank
                {
                    Rank = i + 1,
                    Key = v.Key,
                    Name = v.T.Name,
                    Tag = v.T.Tag,
                    Email = v.T.Email,
                    IsOld = v.Old,
                    LatestText = v.T.LatestMonth is { } m
                        ? PriceAnalysis.Money(ctx.Local, v.T.LatestPrice) + " · " + MonthLabel(m, ctx.ThisMonth) + (v.Old ? ", old" : "")
                        : "Said no",
                    VsText = v.T.LatestMonth is { } m2 ? PriceAnalysis.VsText(v.T.LatestPrice, market.At(m2)) : "-",
                    VsTone = v.T.LatestMonth is { } m3 ? PriceAnalysis.VsTone(v.T.LatestPrice, market.At(m3)) : 0,
                    RepliesText = v.T.Replies == 0 ? "-" : DaysText(v.T.ReplyDays / v.T.Replies),
                    SaidNoText = $"{v.T.Declined} of {v.T.Asked}",
                    BoughtText = v.T.Bought + "×",
                })
                .ToList();

            var from = ctx.SinceMonth ?? Math.Min(market.IsEmpty ? ctx.ThisMonth : market.FirstMonth,
                dots.Count > 0 ? (int)dots.Min(d => d.X) : ctx.ThisMonth);
            var notes = await ReadNotesAsync(connection, itemKey).ConfigureAwait(false);
            var bands = Bands(market, notes, from, ctx.ThisMonth, itemView: true);

            // What buying inside a spike cost, against today's market.
            var now = market.Latest();
            var spikeCost = string.Empty;
            if (now is { } today)
            {
                var inSpike = orders.Where(o => !double.IsNaN(o.Price) && o.Price > today.Value
                                                && bands.Any(b => b.IsSpike && o.Month >= b.FromMonth && o.Month <= b.ToMonth)).ToList();
                var extra = inSpike.Sum(o => (o.Price - today.Value) * o.Qty);
                if (inSpike.Count > 0 && extra >= 1)
                    spikeCost = $"You bought {(inSpike.Count == 1 ? "one lot" : inSpike.Count + " lots")} inside a spike: about " +
                                $"{PriceAnalysis.Money(ctx.Local, extra)} more than at today's market.";
            }

            var boughtQty = orders.Sum(o => o.Qty);
            var period = ctx.SinceMonth is null ? "in all" : $"in {ctx.ThisMonth - ctx.SinceMonth + 1} months";
            var header = string.Join("  ·  ", new[]
            {
                now is { } n ? $"Market now {PriceAnalysis.Money(ctx.Local, n.Value)} per {UnitOf(unit)}" : "No market price yet",
                orders.Count == 0 ? $"not bought {period}" : $"bought {orders.Count} {(orders.Count == 1 ? "time" : "times")}, {boughtQty:N0} {UnitOf(unit)} {period}",
                ranked.Count == 1 ? "1 supplier" : $"{ranked.Count:N0} suppliers",
            });

            return new ItemDetail
            {
                Key = itemKey,
                Name = name,
                HeaderText = header,
                SpikeCostText = spikeCost,
                Suppliers = ranked,
                Notes = notes,
                Aliases = raw.Keys.Where(k => k != itemKey).Select(k => (k, names.TryGetValue(k, out var an) ? an : k)).ToList(),
                Chart = new PriceChartData
                {
                    FromMonth = from,
                    ToMonth = ctx.ThisMonth,
                    Currency = ctx.Local,
                    Unit = UnitOf(unit),
                    Market = market.Points(from, ctx.ThisMonth).ToList(),
                    Dots = dots,
                    Bands = bands,
                    MarketLegend = "Market (middle of that month's quotes)",
                    GreyQuotes = true,
                    ShowHint = true,
                },
            };
        });

        private sealed class VendorTally(string name)
        {
            public string Name = name;
            public string Tag = string.Empty;
            public string Email = string.Empty;
            public int Asked, Declined, Replies, Bought;
            public double ReplyDays, LatestPrice;
            public int? LatestMonth;
        }

        public Task TreatAsOneAsync(string aliasKey, string canonicalKey) => Task.Run(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            // Always point at the end of a chain, and re-point anything that pointed at the alias.
            cmd.CommandText = @"
INSERT INTO ItemAlias (AliasKey, CanonicalKey)
SELECT @A, COALESCE((SELECT CanonicalKey FROM ItemAlias WHERE AliasKey = @C), @C)
WHERE @A <> COALESCE((SELECT CanonicalKey FROM ItemAlias WHERE AliasKey = @C), @C)
ON CONFLICT(AliasKey) DO UPDATE SET CanonicalKey = excluded.CanonicalKey;
UPDATE ItemAlias SET CanonicalKey = (SELECT CanonicalKey FROM ItemAlias WHERE AliasKey = @A) WHERE CanonicalKey = @A;
UPDATE ItemNote SET ItemKey = (SELECT CanonicalKey FROM ItemAlias WHERE AliasKey = @A) WHERE ItemKey = @A
    AND EXISTS (SELECT 1 FROM ItemAlias WHERE AliasKey = @A);";
            cmd.Parameters.AddWithValue("@A", aliasKey);
            cmd.Parameters.AddWithValue("@C", canonicalKey);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            tx.Commit();
            ClearMarkets();
        });

        public Task SeparateAsync(string aliasKey) => ExecuteAsync("DELETE FROM ItemAlias WHERE AliasKey = @A;", true, ("@A", aliasKey));

        public Task AddItemNoteAsync(string itemKey, int fromMonth, int toMonth, string text) => ExecuteAsync(
            "INSERT INTO ItemNote (Id, ItemKey, FromMonth, ToMonth, Text, CreatedAt) VALUES (@Id, @K, @F, @T, @X, @At);",
            ("@Id", Guid.NewGuid().ToString()), ("@K", itemKey), ("@F", MonthIndex.Key(Math.Min(fromMonth, toMonth))),
            ("@T", MonthIndex.Key(Math.Max(fromMonth, toMonth))), ("@X", text.Trim()), ("@At", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)));

        public Task DeleteItemNoteAsync(string id) => ExecuteAsync("DELETE FROM ItemNote WHERE Id = @Id;", ("@Id", id));

        // ---- shared reads ---------------------------------------------------------------------

        private const int MaxDots = 300;

        /// <summary>Keeps an even random sample of at most <see cref="MaxDots"/> from a stream of any
        /// length (reservoir sampling), so a chart covers its whole period in fixed memory. Seeded, so
        /// the same item draws the same dots every time it is opened.</summary>
        private static void Sample(List<ChartDot> kept, ref int seen, Random rng, Func<ChartDot> make)
        {
            seen++;
            if (kept.Count < MaxDots) { kept.Add(make()); return; }
            var slot = rng.Next(seen);
            if (slot < MaxDots) kept[slot] = make();
        }

        /// <summary>Rounds - the same PR, the same item - that this vendor priced and at least one other
        /// vendor priced too, and how many they were cheapest in. Prices in the local currency, so a
        /// USD quote and an AED quote in one round compare fairly; a currency without a rate is left out.</summary>
        private static async Task<(int Count, int Won)> ReadRoundsAsync(SqliteConnection connection, string vendorKey, PriceContext ctx)
        {
            using var cmd = connection.CreateCommand();
            var rate = "CASE " + RfqCurrency + " " + string.Join(" ", ctx.ToLocal.Select((r, i) =>
            {
                cmd.Parameters.AddWithValue("@Cur" + i, r.Key);
                cmd.Parameters.AddWithValue("@Rate" + i, (double)r.Value);
                return $"WHEN @Cur{i} THEN @Rate{i}";
            })) + " END";
            var price = $"{NetQuote} * ({rate})";
            cmd.CommandText = $@"
WITH mine AS (
    SELECT r.PrId AS pr, {Canonical("ri.ItemName")} AS k, min({price}) AS p
    FROM RequestForQuotation AS r JOIN RfqItem AS ri ON ri.RfqId = r.Id
    WHERE lower(trim(r.Vendor)) = @V AND ri.QuoteDate >= @Since AND {Priced}
    GROUP BY 1, 2 HAVING p IS NOT NULL),
others AS (
    SELECT r.PrId AS pr, {Canonical("ri.ItemName")} AS k, min({price}) AS best
    FROM RequestForQuotation AS r JOIN RfqItem AS ri ON ri.RfqId = r.Id
    WHERE r.PrId IN (SELECT pr FROM mine) AND lower(trim(r.Vendor)) <> @V AND {Priced}
    GROUP BY 1, 2 HAVING best IS NOT NULL)
SELECT COUNT(*), COALESCE(SUM(m.p <= o.best * (1 + 1e-9)), 0)
FROM mine AS m JOIN others AS o ON o.pr = m.pr AND o.k = m.k;";
            cmd.Parameters.AddWithValue("@V", vendorKey);
            cmd.Parameters.AddWithValue("@Since", ctx.SinceKey);
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
        }

        // Monthly market per item, cached: a supplier's table and the item list ask for the same
        // items again and again. Cleared by any RFQ/PO write and by "Treat as one item"; a read that
        // started before a clear does not put its (possibly stale) result back.
        private readonly ConcurrentDictionary<string, MarketSeries> _markets = new();
        private int _marketGeneration;

        private void ClearMarkets()
        {
            Interlocked.Increment(ref _marketGeneration);
            _markets.Clear();
        }

        private async Task<Dictionary<string, MarketSeries>> MarketsAsync(SqliteConnection connection, IReadOnlyList<string> itemKeys, PriceContext ctx)
        {
            var result = new Dictionary<string, MarketSeries>();
            var missing = new List<string>();
            foreach (var key in itemKeys.Distinct())
                if (_markets.TryGetValue(key + "|" + ctx.RatesKey, out var m)) result[key] = m;
                else missing.Add(key);
            if (missing.Count == 0) return result;

            var generation = Volatile.Read(ref _marketGeneration);
            var raw = await RawKeysAsync(connection, missing).ConfigureAwait(false);
            var quotes = missing.ToDictionary(k => k, _ => new List<(int, double)>());
            using (var cmd = connection.CreateCommand())
            {
                // Read from IX_RfqItem_Cover alone - no RFQ lookups.
                cmd.CommandText = $@"SELECT lower(trim(ri.ItemName)), ri.QuoteDate, {NetQuote}, ri.QuoteCurrency
FROM RfqItem AS ri
WHERE lower(trim(ri.ItemName)) IN ({InList(cmd, "@R", raw.Keys)}) AND {Priced};";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                    if (MonthOf(reader.GetString(1)) is { } month && ctx.Rate(reader.GetString(3)) is { } rate)
                        quotes[raw[reader.GetString(0)]].Add((month, reader.GetDouble(2) * rate));
            }

            // ponytail: whole-cache reset past 5,000 items; an LRU if people browse more than that per session.
            if (_markets.Count > 5000) _markets.Clear();
            foreach (var (key, list) in quotes)
            {
                var market = PriceAnalysis.Market(list);
                result[key] = market;
                if (Volatile.Read(ref _marketGeneration) == generation) _markets[key + "|" + ctx.RatesKey] = market;
            }
            return result;
        }

        /// <summary>Every stored item name that counts as one of <paramref name="canonical"/>, mapped
        /// to the key it counts as - each key itself plus anything folded into it.</summary>
        private static async Task<Dictionary<string, string>> RawKeysAsync(SqliteConnection connection, IReadOnlyList<string> canonical)
        {
            var map = canonical.Distinct().ToDictionary(k => k, k => k);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT AliasKey, CanonicalKey FROM ItemAlias WHERE CanonicalKey IN ({InList(cmd, "@C", map.Keys.ToList())});";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) map[reader.GetString(0)] = reader.GetString(1);
            return map;
        }

        private static async Task<Dictionary<string, string>> ItemNamesAsync(SqliteConnection connection, IReadOnlyList<string> keys)
        {
            var names = new Dictionary<string, string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT ItemKey, ItemName FROM ItemAggregate WHERE ItemKey IN ({InList(cmd, "@K", keys)});";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) names[reader.GetString(0)] = reader.GetString(1);
            return names;
        }

        private static async Task<List<ItemNoteRow>> ReadNotesAsync(SqliteConnection connection, string itemKey)
        {
            var notes = new List<ItemNoteRow>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, FromMonth, ToMonth, Text FROM ItemNote WHERE ItemKey = @K ORDER BY FromMonth;";
            cmd.Parameters.AddWithValue("@K", itemKey);
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                if (MonthIndex.Parse(reader.GetString(1)) is { } f && MonthIndex.Parse(reader.GetString(2)) is { } t)
                    notes.Add(new ItemNoteRow(reader.GetString(0), f, t, reader.GetString(3)));
            return notes;
        }

        /// <summary>The shaded stretches on a chart: each spike with any notes that overlap it, and
        /// notes that sit outside every spike as bands of their own.</summary>
        private static List<ChartBand> Bands(MarketSeries market, IReadOnlyList<ItemNoteRow> notes, int from, int to, bool itemView)
        {
            var bands = new List<ChartBand>();
            var spikes = PriceAnalysis.Spikes(market, from, to);
            foreach (var s in spikes)
            {
                var title = $"Spike {MonthIndex.Short(s.FromMonth)}" + (s.ToMonth > s.FromMonth ? "–" + MonthIndex.Short(s.ToMonth) : "") +
                            $" · {(itemView ? "market " : "")}+{Math.Round(s.Rise * 100):0}%";
                var note = string.Join("; ", notes.Where(n => n.FromMonth <= s.ToMonth && n.ToMonth >= s.FromMonth).Select(n => n.Text));
                bands.Add(new ChartBand(s.FromMonth, s.ToMonth, title, note, true));
            }
            foreach (var n in notes)
                if (n.ToMonth >= from && n.FromMonth <= to && !spikes.Any(s => n.FromMonth <= s.ToMonth && n.ToMonth >= s.FromMonth))
                    bands.Add(new ChartBand(Math.Max(from, n.FromMonth), Math.Min(to, n.ToMonth), string.Empty, n.Text, false));
            return bands;
        }

        // ---- helpers ------------------------------------------------------------------------------

        private async Task<SqliteConnection> OpenAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            return connection;
        }

        private Task ExecuteAsync(string sql, params (string Name, object Value)[] args) => ExecuteAsync(sql, false, args);

        private Task ExecuteAsync(string sql, bool clearMarkets, params (string Name, object Value)[] args) => Task.Run(async () =>
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (clearMarkets) ClearMarkets();
        });

        /// <summary>Adds one parameter per value and returns their names for an IN list. An empty
        /// list becomes NULL, which matches nothing.</summary>
        private static string InList(SqliteCommand cmd, string prefix, IEnumerable<string> values)
        {
            var names = values.Select((v, i) => { cmd.Parameters.AddWithValue(prefix + i, v); return prefix + i; }).ToList();
            return names.Count == 0 ? "NULL" : string.Join(",", names);
        }

        private static async Task<Dictionary<string, List<SpendLine>>> ReadSpendAsync(SqliteConnection connection, IReadOnlyList<string> keys)
        {
            var result = new Dictionary<string, List<SpendLine>>();
            if (keys.Count == 0) return result;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT VendorKey, Currency, Total FROM VendorSpend WHERE VendorKey IN ({InList(cmd, "@K", keys)});";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                if (!result.TryGetValue(key, out var list)) result[key] = list = new List<SpendLine>();
                list.Add(new SpendLine(reader.GetString(1), (decimal)reader.GetDouble(2)));
            }
            return result;
        }

        private static async Task<int> ScalarAsync(SqliteConnection connection, string sql, string vendorKey, string since)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@K", vendorKey);
            cmd.Parameters.AddWithValue("@Since", since);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }

        /// <summary>The month of a stored date ("2026-03-14T..."), read straight off the text.</summary>
        internal static int? MonthOf(string date) =>
            date.Length >= 7 && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            && int.TryParse(date.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12
                ? y * 12 + m - 1 : null;

        /// <summary>Where a date sits on a chart's month axis: the month plus how far through it.</summary>
        private static double? X(string date)
        {
            if (MonthOf(date) is not { } month) return null;
            var day = date.Length >= 10 && int.TryParse(date.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 15;
            return month + (day - 1) / 31.0;
        }

        private static string WhenText(string date) =>
            date.Length >= 10 && DateTime.TryParseExact(date[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : date;

        /// <summary>"Aug" this year, "Aug 2025" before it.</summary>
        private static string MonthLabel(int month, int thisMonth) =>
            month / 12 == thisMonth / 12 ? MonthIndex.Short(month) : MonthIndex.Long(month);

        private static string DaysText(double days) =>
            days < 0.5 ? "Same day" : Math.Round(days) == 1 ? "1 day" : $"{Math.Round(days):0} days";

        private static string UnitOf(string unit) => string.IsNullOrWhiteSpace(unit) ? "pcs" : unit.Trim();

        /// <summary>"Al Noor" from "Al Noor Industrial Supplies" - the legend has little room.</summary>
        private static string ShortName(string name)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length <= 2 ? name : string.Join(' ', words.Take(2));
        }

        private static string EscapeLike(string text) => text.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");

        private static DateTime? ParseDate(SqliteDataReader r, int i) =>
            !r.IsDBNull(i) && DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                ? d : null;
    }

    /// <summary>What a supplier sells, from the words in the item names they were asked about.</summary>
    internal static class ItemCategories
    {

        private static readonly (string Category, string[] Words)[] Map =
        {
            ("Seals & gaskets", new[] { "seal", "gasket", "oring", "o-ring", "packing" }),
            ("Valves", new[] { "valve" }),
            ("Bearings", new[] { "bearing" }),
            ("Pumps", new[] { "pump", "impeller" }),
            ("Motors & electrical", new[] { "motor", "cable", "breaker", "contactor", "relay", "switch", "lamp", "wire", "fuse" }),
            ("Instruments", new[] { "sensor", "gauge", "transmitter", "thermocouple", "meter" }),
            ("Filters", new[] { "filter" }),
            ("Power transmission", new[] { "coupling", "belt", "chain", "gear", "sprocket", "shaft" }),
            ("Piping", new[] { "pipe", "flange", "elbow", "fitting", "hose" }),
            ("Fasteners", new[] { "bolt", "nut", "screw", "washer", "stud" }),
            ("Oils & chemicals", new[] { "oil", "grease", "lubricant", "chemical", "solvent", "paint" }),
            ("Safety", new[] { "glove", "helmet", "goggle", "mask", "boot", "harness" }),
        };

        /// <summary>Up to four categories, those covering the most of the names first; a category
        /// needs at least a tenth of the names (or one, for a short list) to count.</summary>
        public static IReadOnlyList<string> Of(IReadOnlyList<string> names)
        {
            if (names.Count == 0) return Array.Empty<string>();
            var counts = new Dictionary<string, int>();
            foreach (var name in names)
            {
                var words = name.Split(new[] { ' ', '-', '/', ',', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var (category, keys) in Map)
                    if (words.Any(w => keys.Any(k => w.StartsWith(k, StringComparison.OrdinalIgnoreCase))))
                    {
                        counts[category] = counts.GetValueOrDefault(category) + 1;
                        break;
                    }
            }
            var floor = Math.Max(1, names.Count / 10);
            return counts.Where(c => c.Value >= floor).OrderByDescending(c => c.Value).Take(4).Select(c => c.Key).ToList();
        }
    }
}
