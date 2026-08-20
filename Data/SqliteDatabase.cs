using System;
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

            return new SqliteConnection(DatabaseConstants.ConnectionString);
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

                using var cmd = connection.CreateCommand();
                cmd.CommandText = DatabaseConstants.SqlCreateTables;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Run safe incremental migrations for existing tables
                await MigrateSchemaAsync(connection).ConfigureAwait(false);

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
