using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Procure.Utilities;
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

        /// <summary>How many migration backups to keep. Enough to step back through a couple of
        /// upgrades; not so many that a large database quietly fills the disk.</summary>
        private const int BackupsToKeep = 3;

        /// <summary>Copies the database before a migration touches it, and proves the copy is
        /// readable before letting the migration proceed.
        ///
        /// VACUUM INTO rather than a file copy: the live database has a WAL sidecar holding commits
        /// that are not in the main file yet, so copying the one file can capture a database that is
        /// missing its most recent writes. VACUUM INTO writes a single consistent file with
        /// everything in it.
        ///
        /// If the backup cannot be made or cannot be read back, the migration does NOT run. An app
        /// that refuses to start until there is disk space is a bad morning; a half-migrated database
        /// with no copy of the original is a catastrophe.</summary>
        private static async Task BackupBeforeMigrationAsync(SqliteConnection connection, int storedVersion)
        {
            // A database this app has never stamped is either brand new or empty - nothing to lose,
            // and no reason to make a new user wait on a pointless copy.
            if (storedVersion == 0 && !await HasAnyDataAsync(connection).ConfigureAwait(false)) return;

            var dir = DatabaseConstants.DatabaseDirectory;
            var path = Path.Combine(dir,
                $"procure_tracker.pre-v{DatabaseConstants.SchemaVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.backup.db3");

            try
            {
                if (File.Exists(path)) File.Delete(path);

                using (var cmd = connection.CreateCommand())
                {
                    // The path is quoted as an SQL string literal - VACUUM INTO takes an expression,
                    // not a parameter.
                    cmd.CommandText = "VACUUM INTO '" + path.Replace("'", "''") + "';";
                    cmd.CommandTimeout = 0;   // a large database can take a while; it is once per upgrade
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await VerifyBackupAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
                CrashLog.Write($"Migration backup failed ({storedVersion} -> {DatabaseConstants.SchemaVersion})", ex);
                throw new InvalidOperationException(
                    "Procure needs to update its database, and could not first save a backup copy of it at " +
                    dir + ". The update has not been made and your data has not been changed. " +
                    "Free up disk space (a copy of the database needs about as much room as the database itself) " +
                    "and start Procure again. Details: " + ex.Message, ex);
            }

            CrashLog.Write($"Migration backup written: {Path.GetFileName(path)} " +
                           $"({new FileInfo(path).Length / (1024 * 1024)} MB, schema {storedVersion} -> {DatabaseConstants.SchemaVersion})");
            PruneOldBackups(dir);
        }

        /// <summary>Opens the copy and reads from it. A file of the right size that SQLite cannot open
        /// is not a backup, and finding that out now - while the original is still untouched - is the
        /// whole point.</summary>
        private static async Task VerifyBackupAsync(string path)
        {
            var readOnly = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var check = new SqliteConnection(readOnly);
            await check.OpenAsync().ConfigureAwait(false);

            using var cmd = check.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = (await cmd.ExecuteScalarAsync().ConfigureAwait(false))?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("the backup copy did not pass SQLite's integrity check: " + result);
        }

        private static async Task<bool> HasAnyDataAsync(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='PurchaseRequisition');";
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) == 0) return false;

            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM PurchaseRequisition);";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) == 1;
        }

        /// <summary>Keeps the newest <see cref="BackupsToKeep"/> and removes the rest.</summary>
        private static void PruneOldBackups(string dir)
        {
            try
            {
                var old = new DirectoryInfo(dir)
                    .GetFiles("procure_tracker.pre-v*.backup.db3")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(BackupsToKeep);
                foreach (var f in old) f.Delete();
            }
            catch
            {
                // Housekeeping only - never worth failing a launch over.
            }
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
                    // Before anything is altered: a copy of the database as it is right now. A
                    // migration rewrites tables and, from v19, drops a column - all irreversible on
                    // the file itself, and a machine that loses power or runs out of disk halfway
                    // through leaves it in a state nobody can reason about. This is the only thing
                    // standing between an upgrade going wrong and a colleague losing years of
                    // procurement history.
                    await BackupBeforeMigrationAsync(connection, storedVersion).ConfigureAwait(false);

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = DatabaseConstants.SqlCreateTables;
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // Separate statement: a virtual table cannot be created inside the same batch
                    // that defines the tables it reads.
                    using (var ftsCreate = connection.CreateCommand())
                    {
                        ftsCreate.CommandText = DatabaseConstants.SqlCreateSearchIndex;
                        await ftsCreate.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // Run safe incremental migrations for existing tables
                    await MigrateSchemaAsync(connection, storedVersion).ConfigureAwait(false);

                    // MaterialAggregate is derived: v13 introduced it, v15 added LastActivity, and
                    // v16 re-runs the fill because a v15 build shipped without the ALTER, leaving
                    // databases stamped current with the column missing. From then on the write
                    // paths keep it in step.
                    if (storedVersion < 16)
                    {
                        using var aggCmd = connection.CreateCommand();
                        aggCmd.CommandText = DatabaseConstants.SqlRebuildAllMaterialAggregates;
                        await aggCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // v18: the ranked search index. Derived like the aggregate above, so an existing
                    // database has to have it built once here.
                    if (storedVersion < 18)
                    {
                        using var ftsCmd = connection.CreateCommand();
                        ftsCmd.CommandText = DatabaseConstants.SqlRebuildSearchIndex;
                        await ftsCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // v19: SearchBlob is gone. Nothing had read the column since the FTS index
                    // landed in v18, but every PR write still recomputed it and the changed-rows
                    // rebuild evaluated its expression twice per row. Dropping it reclaims a second
                    // copy of every searchable word in the file.
                    if (storedVersion < 19)
                    {
                        using var dropCmd = connection.CreateCommand();
                        dropCmd.CommandText = "ALTER TABLE PurchaseRequisition DROP COLUMN SearchBlob;";
                        try { await dropCmd.ExecuteNonQueryAsync().ConfigureAwait(false); }
                        catch (SqliteException) { /* already gone: a database created at v19 never had it */ }
                    }

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
            await EnsureColumnExistsAsync(connection, "MaterialAggregate", "LastActivity", "TEXT").ConfigureAwait(false);
            await EnsureColumnExistsAsync(connection, "PurchaseOrder", "TransportMode", "TEXT").ConfigureAwait(false);
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

            // v14: reattach quote and PO lines that were never given a PR line to point at (the
            // merge used to drop the link), then give combined POs the line items they never got.
            // Order matters - the backfill copies PrItemId off the quote lines repaired above.
            if (fromVersion < 14)
            {
                foreach (var repairSql in new[]
                         {
                             DatabaseConstants.SqlRelinkOrphanedRfqItems,
                             DatabaseConstants.SqlRelinkOrphanedPoItems,
                             DatabaseConstants.SqlBackfillCombinedPoItems,
                         })
                {
                    using var relink = connection.CreateCommand();
                    relink.CommandText = repairSql;
                    await relink.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // The backfill above adds PO item rows, which are what MaterialAggregate summarises.
                // Skipping this leaves the Raw & Packing tab reporting figures that predate the
                // repair - the exact staleness DatabaseSelfCheck asserts against.
                using var aggregates = connection.CreateCommand();
                aggregates.CommandText = DatabaseConstants.SqlRebuildAllMaterialAggregates;
                await aggregates.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

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
    }
}
