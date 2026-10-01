using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;

namespace Procure.Data.Repositories
{
    /// <summary>The Service Entry register. One table, no links to anything else.
    ///
    /// Filters: <c>stage</c> is "open" (not yet with accounts), "all", or a stage number "0".."3".</summary>
    public sealed class ServiceEntryRepository
    {
        private const string Columns =
            "Id, SrNo, PoNo, PoAmount, Vendor, Description, InvoiceDate, InvoiceNo, InvoiceAmount, " +
            "TechHandoverDate, SapSeDate, ServiceEntryNo, AccountHandoverDate";

        private readonly SqliteDatabase _db;
        public ServiceEntryRepository(SqliteDatabase db) => _db = db;

        private async Task<SqliteConnection> OpenAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var c = _db.CreateConnection();
            await c.OpenAsync().ConfigureAwait(false);
            return c;
        }

        // ponytail: LIKE '%term%' scans the table. Fine into six figures of rows; add an FTS table
        // (as the PR board has) if the register ever gets near a million.
        private static string Where(SqliteCommand cmd, string stage, string? search)
        {
            var w = new List<string>();
            if (stage == "open") w.Add("Stage < 3");
            else if (int.TryParse(stage, out var s)) { w.Add("Stage = @Stage"); cmd.Parameters.AddWithValue("@Stage", s); }
            if (!string.IsNullOrWhiteSpace(search))
            {
                const string like = " LIKE @Q ESCAPE '\\'";
                w.Add($"(PoNo{like} OR Vendor{like} OR InvoiceNo{like} OR ServiceEntryNo{like} OR Description{like} OR CAST(SrNo AS TEXT) = @Exact)");
                var t = search.Trim();
                cmd.Parameters.AddWithValue("@Q", "%" + t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
                cmd.Parameters.AddWithValue("@Exact", t);
            }
            return w.Count == 0 ? "" : " WHERE " + string.Join(" AND ", w);
        }

        /// <summary>Newest Sr No first, one page.</summary>
        public async Task<List<ServiceEntry>> GetPageAsync(string stage, string? search, int skip, int take)
        {
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM ServiceEntry{Where(cmd, stage, search)} ORDER BY SrNo DESC LIMIT @Take OFFSET @Skip;";
            cmd.Parameters.AddWithValue("@Take", take);
            cmd.Parameters.AddWithValue("@Skip", skip);
            return await ReadAllAsync(cmd).ConfigureAwait(false);
        }

        /// <summary>Every row the filter matches, oldest Sr No first, for the Excel export.</summary>
        public async Task<List<ServiceEntry>> GetAllAsync(string stage, string? search)
        {
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM ServiceEntry{Where(cmd, stage, search)} ORDER BY SrNo;";
            return await ReadAllAsync(cmd).ConfigureAwait(false);
        }

        public async Task<ServiceEntrySummary> GetSummaryAsync(int flagDays)
        {
            var sum = new ServiceEntrySummary();
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            var today = DateTime.Today;
            var monthStart = new DateTime(today.Year, today.Month, 1);
            cmd.CommandText = @"
SELECT Stage, COUNT(*), COALESCE(SUM(InvoiceAmount), 0),
       SUM(CASE WHEN Stage < 3 AND StageDate < @LateBefore THEN 1 ELSE 0 END),
       SUM(CASE WHEN AccountHandoverDate >= @MonthStart THEN 1 ELSE 0 END),
       COALESCE(SUM(CASE WHEN AccountHandoverDate >= @MonthStart THEN InvoiceAmount END), 0)
FROM ServiceEntry GROUP BY Stage;";
            cmd.Parameters.AddWithValue("@LateBefore", D(today.AddDays(-flagDays)));
            cmd.Parameters.AddWithValue("@MonthStart", D(monthStart));
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                var stage = r.GetInt32(0);
                sum.StageCounts[stage] = r.GetInt32(1);
                if (stage < 3) sum.OpenAmount += (decimal)r.GetDouble(2);
                sum.LateCount += r.GetInt32(3);
                sum.SentThisMonth += r.GetInt32(4);
                sum.SentThisMonthAmount += (decimal)r.GetDouble(5);
            }
            return sum;
        }

        public async Task<int> NextSrNoAsync()
        {
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(SrNo), 0) + 1 FROM ServiceEntry;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }

        public async Task<bool> SrNoTakenAsync(int srNo, Guid exceptId)
        {
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM ServiceEntry WHERE SrNo = @Sr AND Id <> @Id);";
            cmd.Parameters.AddWithValue("@Sr", srNo);
            cmd.Parameters.AddWithValue("@Id", exceptId.ToString());
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) == 1;
        }

        /// <summary>How many invoices are on this PO number and their total, this register only.</summary>
        public async Task<(int Count, decimal Total)> GetPoBilledAsync(string poNo, Guid exceptId)
        {
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(InvoiceAmount), 0) FROM ServiceEntry WHERE PoNo = @Po COLLATE NOCASE AND Id <> @Id;";
            cmd.Parameters.AddWithValue("@Po", poNo.Trim());
            cmd.Parameters.AddWithValue("@Id", exceptId.ToString());
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            await r.ReadAsync().ConfigureAwait(false);
            return (r.GetInt32(0), (decimal)r.GetDouble(1));
        }

        /// <summary>Vendors typed on this tab before, for the Vendor box.</summary>
        public async Task<List<string>> FindVendorsAsync(string prefix)
        {
            var list = new List<string>();
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT Vendor FROM ServiceEntry WHERE Vendor LIKE @P ESCAPE '\\' ORDER BY Vendor COLLATE NOCASE LIMIT 8;";
            cmd.Parameters.AddWithValue("@P", prefix.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false)) list.Add(r.GetString(0));
            return list;
        }

        public async Task SaveAsync(ServiceEntry e)
        {
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ServiceEntry (Id, SrNo, PoNo, PoAmount, Vendor, Description, InvoiceDate, InvoiceNo, InvoiceAmount,
    TechHandoverDate, SapSeDate, ServiceEntryNo, AccountHandoverDate, CreatedAt, UpdatedAt)
VALUES (@Id, @SrNo, @PoNo, @PoAmount, @Vendor, @Description, @InvoiceDate, @InvoiceNo, @InvoiceAmount,
    @Tech, @SapSe, @SeNo, @Acc, @Now, @Now)
ON CONFLICT(Id) DO UPDATE SET SrNo = excluded.SrNo, PoNo = excluded.PoNo, PoAmount = excluded.PoAmount,
    Vendor = excluded.Vendor, Description = excluded.Description, InvoiceDate = excluded.InvoiceDate,
    InvoiceNo = excluded.InvoiceNo, InvoiceAmount = excluded.InvoiceAmount, TechHandoverDate = excluded.TechHandoverDate,
    SapSeDate = excluded.SapSeDate, ServiceEntryNo = excluded.ServiceEntryNo,
    AccountHandoverDate = excluded.AccountHandoverDate, UpdatedAt = excluded.UpdatedAt;";
            cmd.Parameters.AddWithValue("@Id", e.Id.ToString());
            cmd.Parameters.AddWithValue("@SrNo", e.SrNo);
            cmd.Parameters.AddWithValue("@PoNo", e.PoNo.Trim());
            cmd.Parameters.AddWithValue("@PoAmount", e.PoAmount is { } p ? (double)p : DBNull.Value);
            cmd.Parameters.AddWithValue("@Vendor", e.Vendor.Trim());
            cmd.Parameters.AddWithValue("@Description", e.Description.Trim());
            cmd.Parameters.AddWithValue("@InvoiceDate", D(e.InvoiceDate));
            cmd.Parameters.AddWithValue("@InvoiceNo", e.InvoiceNo.Trim());
            cmd.Parameters.AddWithValue("@InvoiceAmount", (double)e.InvoiceAmount);
            cmd.Parameters.AddWithValue("@Tech", N(e.TechHandoverDate));
            cmd.Parameters.AddWithValue("@SapSe", N(e.SapSeDate));
            cmd.Parameters.AddWithValue("@SeNo", string.IsNullOrWhiteSpace(e.ServiceEntryNo) ? DBNull.Value : e.ServiceEntryNo.Trim());
            cmd.Parameters.AddWithValue("@Acc", N(e.AccountHandoverDate));
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task DeleteAsync(Guid id)
        {
            using var c = await OpenAsync().ConfigureAwait(false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM ServiceEntry WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task<List<ServiceEntry>> ReadAllAsync(SqliteCommand cmd)
        {
            var list = new List<ServiceEntry>();
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new ServiceEntry
                {
                    Id = Guid.Parse(r.GetString(0)),
                    SrNo = r.GetInt32(1),
                    PoNo = r.GetString(2),
                    PoAmount = r.IsDBNull(3) ? null : (decimal)r.GetDouble(3),
                    Vendor = r.GetString(4),
                    Description = r.GetString(5),
                    InvoiceDate = P(r.GetString(6)) ?? DateTime.Today,
                    InvoiceNo = r.GetString(7),
                    InvoiceAmount = (decimal)r.GetDouble(8),
                    TechHandoverDate = r.IsDBNull(9) ? null : P(r.GetString(9)),
                    SapSeDate = r.IsDBNull(10) ? null : P(r.GetString(10)),
                    ServiceEntryNo = r.IsDBNull(11) ? null : r.GetString(11),
                    AccountHandoverDate = r.IsDBNull(12) ? null : P(r.GetString(12)),
                });
            }
            return list;
        }

        private static string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private static object N(DateTime? d) => d is { } x ? D(x) : DBNull.Value;
        private static DateTime? P(string s) =>
            DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
