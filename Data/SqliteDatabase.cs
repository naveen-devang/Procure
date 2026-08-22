using System;
using System.Data;
using System.IO;
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
            var dir = DatabaseConstants.DatabaseDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

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
                if (await ReadSchemaVersionAsync(connection).ConfigureAwait(false) != DatabaseConstants.SchemaVersion)
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = DatabaseConstants.SqlCreateTables;
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // Run safe incremental migrations for existing tables
                    await MigrateSchemaAsync(connection).ConfigureAwait(false);
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

        private static async Task MigrateSchemaAsync(SqliteConnection connection)
        {
            await EnsureColumnExistsAsync(connection, "PurchaseRequisition", "Plant", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseRequisition", "PrType", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "Freight", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "Warranty", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "TechnicalApproval", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RequestForQuotation", "Discount", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RfqItem", "Discount", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "RfqItem", "LastPrice", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PriceComparisonRequest", "Remarks", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "Currency", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "BaseAmount", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "Freight", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "OtherCharges", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "Discount", "REAL").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "VatType", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrderItem", "SortOrder", "INTEGER").ConfigureAwait(false);
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
