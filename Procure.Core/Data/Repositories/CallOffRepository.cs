using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public class CallOffRepository : ICallOffRepository
    {
        private readonly SqliteDatabase _db;

        public CallOffRepository(SqliteDatabase db)
        {
            _db = db;
        }

        // The tab shows a list of collapsed materials, so that is what loads: one row per
        // material, aggregated in SQL. Loading every eligible PO item up front instead cost 506ms
        // of query plus ~48,000 object constructions on a 20,000-PR database, for rows that were
        // mostly never shown - a material's lines now arrive when it is expanded.
        //
        // Search matches material, vendor or PO number but still returns one row per material: a
        // vendor-name hit has to surface the material group that contains it.
        private const string EligibleLines = @"
FROM PurchaseOrderItem poi
JOIN PurchaseOrder po ON poi.PoId = po.Id
JOIN PurchaseRequisition pr ON po.PrId = pr.Id
WHERE pr.PrType IN ('Raw Material', 'Packing Material')";

        public async Task<List<MaterialGroupSummary>> GetMaterialSummariesAsync(string? searchTerm = null)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<MaterialGroupSummary>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var term = searchTerm?.Trim();
            var filtered = !string.IsNullOrEmpty(term);

            using var cmd = connection.CreateCommand();
            if (!filtered)
            {
                // Unfiltered, this is a straight read of the denormalised table - O(materials),
                // independent of how many PO lines exist. Computing it live was one GROUP BY over
                // every eligible line: 244ms at 20,000 PRs, and linear from there.
                cmd.CommandText = @"
SELECT MaterialName, LineCount, TotalOrdered, TotalCalledOff, Unit
FROM MaterialAggregate
ORDER BY MaterialName COLLATE NOCASE ASC;";
            }
            else
            {
                // A search matches vendor and PO number as well as the material name, which the
                // aggregate cannot answer - those live on the lines. So a filtered view is computed,
                // but only over the lines the term matches rather than all of them.
                cmd.CommandText = @"
SELECT TRIM(poi.ItemName) AS M,
       COUNT(*),
       COALESCE(SUM(poi.Quantity), 0),
       COALESCE(SUM((SELECT COALESCE(SUM(Quantity), 0) FROM PoItemCallOff WHERE PoItemId = poi.Id)), 0),
       MIN(COALESCE(NULLIF(poi.Unit, ''), 'pcs'))
" + EligibleLines + @"
  AND (poi.ItemName LIKE @q ESCAPE '\' OR po.Vendor LIKE @q ESCAPE '\' OR po.PoNo LIKE @q ESCAPE '\')
GROUP BY M COLLATE NOCASE
ORDER BY M COLLATE NOCASE ASC;";
                cmd.Parameters.AddWithValue("@q", "%" + EscapeLike(term!) + "%");
            }

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new MaterialGroupSummary(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? "pcs" : reader.GetString(4)));
            }

            return list;
        }

        /// <summary>One page of an expanded material's lines, ordered by how far behind on delivery
        /// each one is, so the first page is the page worth reading. The search term is passed back in
        /// so an expanded group lists exactly the lines the search counted.
        ///
        /// Paged because a material can hold hundreds of PO lines - ~480 in the 20,000-PR database -
        /// and the card used to build every one of them the moment it opened.</summary>
        public async Task<List<CallOffLine>> GetLinesForMaterialAsync(string materialName, string? searchTerm = null, int skip = 0, int take = int.MaxValue)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<CallOffLine>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var term = searchTerm?.Trim();
            var filtered = !string.IsNullOrEmpty(term);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT poi.Id, poi.ItemName, poi.Quantity, poi.Unit, po.Vendor, po.PoNo,
       COALESCE((SELECT SUM(Quantity) FROM PoItemCallOff WHERE PoItemId = poi.Id), 0), pr.PrNo,
       pr.PrType, po.TransportContractNumber, po.TransporterName, po.TransportRatePerUnit, po.TransportTotal, po.Currency
" + EligibleLines + @"
  AND TRIM(poi.ItemName) = @material COLLATE NOCASE" + (filtered
                ? @"
  AND (poi.ItemName LIKE @q ESCAPE '\' OR po.Vendor LIKE @q ESCAPE '\' OR po.PoNo LIKE @q ESCAPE '\')"
                : string.Empty) + @"
ORDER BY (CASE WHEN poi.Quantity > 0
               THEN MIN(1.0, COALESCE((SELECT SUM(Quantity) FROM PoItemCallOff WHERE PoItemId = poi.Id), 0) / poi.Quantity)
               ELSE 0 END) ASC,
         po.Vendor ASC
LIMIT @take OFFSET @skip;";

            cmd.Parameters.AddWithValue("@material", materialName.Trim());
            cmd.Parameters.AddWithValue("@take", take);
            cmd.Parameters.AddWithValue("@skip", skip);
            if (filtered) cmd.Parameters.AddWithValue("@q", "%" + EscapeLike(term!) + "%");

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(ReadLine(reader));
            }

            return list;
        }

        private static CallOffLine ReadLine(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
        {
            PoItemId = Guid.Parse(reader.GetString(0)),
            MaterialName = reader.GetString(1),
            OrderedQuantity = reader.GetDecimal(2),
            Unit = reader.IsDBNull(3) ? "pcs" : reader.GetString(3),
            Vendor = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            PoNo = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            CalledOffQuantity = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
            PrNo = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            PrType = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            TransportContractNumber = reader.IsDBNull(9) ? null : reader.GetString(9),
            TransporterName = reader.IsDBNull(10) ? null : reader.GetString(10),
            TransportRatePerUnit = reader.IsDBNull(11) ? null : (decimal?)reader.GetDouble(11),
            TransportTotal = reader.IsDBNull(12) ? null : (decimal?)reader.GetDouble(12),
            Currency = reader.IsDBNull(13) ? "AED" : reader.GetString(13)
        };

        // The typed term is a substring to find, not a pattern to honour.
        private static string EscapeLike(string term) =>
            term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

        // Lazy per-material log, fetched only when a line is selected - the one piece of this
        // feature that grows without bound over time, per the artifact's scale argument.
        public async Task<List<PoItemCallOff>> GetHistoryAsync(Guid poItemId)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<PoItemCallOff>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, PoItemId, CallOffDate, Quantity, Note FROM PoItemCallOff WHERE PoItemId = @PoItemId ORDER BY CallOffDate DESC;";
            cmd.Parameters.AddWithValue("@PoItemId", poItemId.ToString());

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new PoItemCallOff
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    PoItemId = Guid.Parse(reader.GetString(1)),
                    CallOffDate = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    Quantity = reader.GetDecimal(3),
                    Note = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }

            return list;
        }

        public Task LogCallOffAsync(PoItemCallOff entry) => Task.Run(() => LogCallOffCoreAsync(entry));

        private async Task LogCallOffCoreAsync(PoItemCallOff entry)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO PoItemCallOff (Id, PoItemId, CallOffDate, Quantity, Note) VALUES (@Id, @PoItemId, @CallOffDate, @Quantity, @Note);";
            cmd.Parameters.AddWithValue("@Id", entry.Id.ToString());
            cmd.Parameters.AddWithValue("@PoItemId", entry.PoItemId.ToString());
            cmd.Parameters.AddWithValue("@CallOffDate", entry.CallOffDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
            cmd.Parameters.AddWithValue("@Note", (object?)entry.Note ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            // The material's called-off total is denormalised into MaterialAggregate; without this
            // the collapsed card keeps showing the balance from before this call-off.
            var key = await MaterialAggregateMaintenance.KeyForPoItemAsync(connection, null, entry.PoItemId).ConfigureAwait(false);
            if (key != null)
                await MaterialAggregateMaintenance.RefreshKeysAsync(connection, null, new[] { key }).ConfigureAwait(false);
        }

        public Task DeleteCallOffAsync(Guid id) => Task.Run(() => DeleteCallOffCoreAsync(id));

        private async Task DeleteCallOffCoreAsync(Guid id)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            // The owning PO item has to be read before the row goes, or there is nothing left to
            // resolve the material from.
            string? key;
            using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = @"
SELECT lower(TRIM(poi.ItemName))
FROM PoItemCallOff co JOIN PurchaseOrderItem poi ON poi.Id = co.PoItemId
WHERE co.Id = @Id;";
                lookup.Parameters.AddWithValue("@Id", id.ToString());
                key = await lookup.ExecuteScalarAsync().ConfigureAwait(false) as string;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM PoItemCallOff WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (key != null)
                await MaterialAggregateMaintenance.RefreshKeysAsync(connection, null, new[] { key }).ConfigureAwait(false);
        }
    }
}
