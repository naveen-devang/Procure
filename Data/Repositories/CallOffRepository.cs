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

        // The one full load this tab ever does - see the artifact's scale argument: a few hundred
        // PO items, total, ever. PrType filters to Raw/Packing Material the same way the rest of the
        // app already reads PrType off PurchaseRequisition.
        public async Task<List<CallOffLine>> GetAllCallOffLinesAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<CallOffLine>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT poi.Id, poi.ItemName, poi.Quantity, poi.Unit, po.Vendor, po.PoNo,
       COALESCE((SELECT SUM(Quantity) FROM PoItemCallOff WHERE PoItemId = poi.Id), 0), pr.PrNo,
       pr.PrType, po.TransportContractNumber, po.TransporterName, po.TransportRatePerUnit, po.TransportTotal, po.Currency
FROM PurchaseOrderItem poi
JOIN PurchaseOrder po ON poi.PoId = po.Id
JOIN PurchaseRequisition pr ON po.PrId = pr.Id
WHERE pr.PrType IN ('Raw Material', 'Packing Material')
ORDER BY poi.ItemName ASC;";

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new CallOffLine
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
                });
            }

            return list;
        }

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
        }

        public Task DeleteCallOffAsync(Guid id) => Task.Run(() => DeleteCallOffCoreAsync(id));

        private async Task DeleteCallOffCoreAsync(Guid id)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM PoItemCallOff WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
