using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Procure.Data
{
    public class SqliteDatabase
    {
        private readonly ILogger<SqliteDatabase>? _logger;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _initialized;

        public SqliteDatabase(ILogger<SqliteDatabase>? logger = null)
        {
            _logger = logger;
        }

        public SqliteConnection CreateConnection()
        {
            // Directory existence is ensured once, when DatabaseConstants resolves its cached path.
            var connection = new SqliteConnection(DatabaseConstants.ConnectionString);

            // Every caller creates a connection and opens it itself, so hooking the open is the one
            // place that reaches all of them - including any added later. The handler is static and
            // captures nothing, and it dies with the connection the caller disposes.
            connection.StateChange += ApplyConnectionPragmas;
            return connection;
        }

        private static void ApplyConnectionPragmas(object? sender, StateChangeEventArgs e)
        {
            if (e.CurrentState != ConnectionState.Open || sender is not SqliteConnection connection) return;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = DatabaseConstants.SqlConnectionPragmas;
            cmd.ExecuteNonQuery();
        }

        public async Task InitializeAsync()
        {
            // Fast path — avoid lock overhead once initialized
            if (_initialized) return;

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock
                if (_initialized) return;

                using var connection = CreateConnection();
                await connection.OpenAsync().ConfigureAwait(false);

                // A database already stamped with the current schema version needs none of the work
                // below. Skipping it avoids re-running the CREATE script plus one PRAGMA
                // table_info round-trip per migrated column on every single launch.
                var storedVersion = await ReadSchemaVersionAsync(connection).ConfigureAwait(false);
                if (storedVersion != DatabaseConstants.SchemaVersion)
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = DatabaseConstants.SqlCreateTables;
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // Run safe incremental migrations for existing tables
                    await MigrateSchemaAsync(connection, storedVersion).ConfigureAwait(false);
                    await WriteSchemaVersionAsync(connection).ConfigureAwait(false);
                }

                _initialized = true;
                _logger?.LogInformation("Database initialized successfully at: {Path}", DatabaseConstants.DatabaseFilePath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize database");
                throw;
            }
            finally
            {
                _initLock.Release();
            }
        }

        private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result is null or DBNull ? 0 : Convert.ToInt32(result);
        }

        private static async Task WriteSchemaVersionAsync(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            // PRAGMA user_version does not accept a parameter, and the value is a compile-time
            // constant rather than anything user supplied.
            cmd.CommandText = $"PRAGMA user_version = {DatabaseConstants.SchemaVersion};";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task MigrateSchemaAsync(SqliteConnection connection, int fromVersion)
        {
            await EnsureColumnExistsAsync(connection, "PurchaseRequisition", "Plant", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseRequisition", "PrType", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "Freight", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "Warranty", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "TechnicalApproval", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "Discount", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "OtherCharges", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "VatType", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "Currency", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "SharedPrs", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RfqItem", "Discount", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RfqItem", "LastPrice", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PriceComparisonRequest", "Remarks", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "Currency", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "BaseAmount", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "Freight", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "OtherCharges", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "Discount", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "VatType", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "TransportContractNumber", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "TransporterName", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "TransportRatePerUnit", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "TransportTotal", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrderItem", "SortOrder", "INTEGER").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseRequisition", "SearchBlob", "TEXT").ConfigureAwait(false);
            // TodoTask.LinkedEntityLabel was added after v7 shipped the table - existing v7 databases
            // have the table already, so the CREATE script skips it; add the column explicitly.
            await EnsureColumnExistsAsync(connection, "TodoTask", "LinkedEntityLabel", "TEXT").ConfigureAwait(false);

            // v9: the single LinkedEntity* columns became the TodoTaskLink table (many links per task).
            // Backfill once, from the pre-v9 single link. INSERT OR IGNORE keeps a re-run harmless.
            if (fromVersion is >= 7 and < 9)
            using (var backfillLinks = connection.CreateCommand())
            {
                backfillLinks.CommandText = @"
INSERT OR IGNORE INTO TodoTaskLink (TaskId, EntityType, EntityId, EntityLabel)
SELECT Id, COALESCE(NULLIF(LinkedEntityType, ''), 'PR'), LinkedEntityId, LinkedEntityLabel
FROM TodoTask
WHERE LinkedEntityId IS NOT NULL AND LinkedEntityId <> '';";
                await backfillLinks.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // One-time repair: the PO wizard's VAT-picker bug saved Value without VAT while the
            // row still records a VatType, leaving Value inconsistent with its own breakdown.
            // Recompute Value from the stored components wherever it deviates; rows without a
            // BaseAmount (legacy rows) carry no breakdown and are left untouched. Gated to the
            // v4 upgrade only — later schema bumps must not re-run it over rows written after
            // the fix, whose component rounding can legitimately differ by a cent or two.
            if (fromVersion < 4)
            using (var repair = connection.CreateCommand())
            {
                repair.CommandText = @"
UPDATE PurchaseOrder
SET Value = ROUND(MAX(0, COALESCE(BaseAmount,0) + COALESCE(Freight,0) + COALESCE(OtherCharges,0) - COALESCE(Discount,0))
            * (CASE WHEN COALESCE(VatType,'5%') = '5%' THEN 1.05 ELSE 1.0 END), 2)
WHERE COALESCE(BaseAmount, 0) > 0
  AND ABS(Value - MAX(0, COALESCE(BaseAmount,0) + COALESCE(Freight,0) + COALESCE(OtherCharges,0) - COALESCE(Discount,0))
            * (CASE WHEN COALESCE(VatType,'5%') = '5%' THEN 1.05 ELSE 1.0 END)) > 0.01;";
                await repair.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Backfill the search text for every existing row. Only reached when the schema version
            // moved, so this runs once per database, not once per launch. ~240ms at 20,000 PRs.
            using var backfill = connection.CreateCommand();
            backfill.CommandText = DatabaseConstants.SqlRebuildSearchBlob + ";";
            await backfill.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task EnsureColumnExistsAsync(SqliteConnection connection, string tableName, string columnName, string columnType)
        {
            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = await pragmaCmd.ExecuteReaderAsync().ConfigureAwait(false);
            bool columnExists = false;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var name = reader.GetString(1);
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    columnExists = true;
                    break;
                }
            }
            reader.Close();

            if (!columnExists)
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType};";
                await alterCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        public void ResetInitialization()
        {
            _initialized = false;
        }
    }
}
