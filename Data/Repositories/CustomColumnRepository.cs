using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public class CustomColumnRepository : ICustomColumnRepository
    {
        private readonly SqliteDatabase _db;

        public CustomColumnRepository(SqliteDatabase db)
        {
            _db = db;
        }

        public async Task<List<CustomColumnDefinition>> GetAllDefinitionsAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<CustomColumnDefinition>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, DataType, SelectOptions, SortOrder FROM CustomColumnDefinition ORDER BY SortOrder ASC, Name ASC;";

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new CustomColumnDefinition
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Name = reader.GetString(1),
                    DataType = reader.GetString(2),
                    SelectOptions = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SortOrder = reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
                });
            }

            return list;
        }

        public async Task SaveDefinitionAsync(CustomColumnDefinition definition)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
INSERT INTO CustomColumnDefinition (Id, Name, DataType, SelectOptions, SortOrder)
VALUES (@Id, @Name, @DataType, @SelectOptions, @SortOrder)
ON CONFLICT(Id) DO UPDATE SET
    Name = excluded.Name,
    DataType = excluded.DataType,
    SelectOptions = excluded.SelectOptions,
    SortOrder = excluded.SortOrder;";

            cmd.Parameters.AddWithValue("@Id", definition.Id.ToString());
            cmd.Parameters.AddWithValue("@Name", definition.Name);
            cmd.Parameters.AddWithValue("@DataType", definition.DataType);
            cmd.Parameters.AddWithValue("@SelectOptions", (object?)definition.SelectOptions ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SortOrder", definition.SortOrder);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task DeleteDefinitionAsync(Guid id)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM CustomColumnDefinition WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task<List<CustomFieldValue>> GetValuesForPrAsync(Guid prId)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<CustomFieldValue>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT v.Id, v.PrId, v.ColumnId, v.Value, d.Name, d.DataType, d.SelectOptions
FROM CustomFieldValue v
JOIN CustomColumnDefinition d ON v.ColumnId = d.Id
WHERE v.PrId = @PrId
ORDER BY d.SortOrder ASC, d.Name ASC;";
            cmd.Parameters.AddWithValue("@PrId", prId.ToString());

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new CustomFieldValue
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    PrId = Guid.Parse(reader.GetString(1)),
                    ColumnId = Guid.Parse(reader.GetString(2)),
                    Value = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ColumnName = reader.GetString(4),
                    ColumnDataType = reader.GetString(5),
                    SelectOptions = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }

            return list;
        }

        public async Task SaveValuesForPrAsync(Guid prId, IEnumerable<CustomFieldValue> values)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            using var delCmd = connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM CustomFieldValue WHERE PrId = @PrId;";
            delCmd.Parameters.AddWithValue("@PrId", prId.ToString());
            await delCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            foreach (var val in values)
            {
                if (string.IsNullOrWhiteSpace(val.Value)) continue;

                using var insCmd = connection.CreateCommand();
                insCmd.Transaction = tx;
                insCmd.CommandText = @"
INSERT INTO CustomFieldValue (Id, PrId, ColumnId, Value)
VALUES (@Id, @PrId, @ColumnId, @Value);";
                insCmd.Parameters.AddWithValue("@Id", val.Id == Guid.Empty ? Guid.NewGuid().ToString() : val.Id.ToString());
                insCmd.Parameters.AddWithValue("@PrId", prId.ToString());
                insCmd.Parameters.AddWithValue("@ColumnId", val.ColumnId.ToString());
                insCmd.Parameters.AddWithValue("@Value", val.Value);

                await insCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }
    }
}
