using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Procure.Data.Repositories
{
    /// <summary>
    /// Keeps <c>MaterialAggregate</c> in step with the PO items and call-offs it summarises.
    ///
    /// Same arrangement, and the same hazard, as the denormalised SearchBlob column: it is derived
    /// data maintained by hand, so every write path that touches a PR's POs, its PO items, or a
    /// call-off has to end here. Miss one and nothing breaks loudly - the Raw &amp; Packing tab just
    /// shows a stale count or balance. <see cref="DatabaseConstants.SqlStaleMaterialAggregateCount"/>
    /// is the guard against that, asserted by DatabaseSelfCheck.
    ///
    /// The calls are deliberately placed next to the existing RefreshSearchBlobAsync calls, because
    /// those already mark every write that can affect this.
    /// </summary>
    internal static class MaterialAggregateMaintenance
    {
        /// <summary>The material keys this PR's PO items currently name.
        ///
        /// Captured *before* a write, not after: renaming or deleting a line changes the totals of
        /// the material it moved away from, and once the write has happened there is nothing left to
        /// point at it. The keys from before and after are refreshed together.</summary>
        public static async Task<List<string>> KeysForPrAsync(SqliteConnection connection, SqliteTransaction? tx, Guid prId)
        {
            var keys = new List<string>();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = DatabaseConstants.SqlMaterialKeysForPr;
            cmd.Parameters.AddWithValue("@PrId", prId.ToString());

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                if (!reader.IsDBNull(0)) keys.Add(reader.GetString(0));

            return keys;
        }

        public static async Task<string?> KeyForPoItemAsync(SqliteConnection connection, SqliteTransaction? tx, Guid poItemId)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = DatabaseConstants.SqlMaterialKeyForPoItem;
            cmd.Parameters.AddWithValue("@PoItemId", poItemId.ToString());
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result as string;
        }

        /// <summary>Recomputes the given keys from the live rows. A key with no eligible lines left
        /// loses its aggregate row rather than keeping a stale one.</summary>
        public static async Task RefreshKeysAsync(SqliteConnection connection, SqliteTransaction? tx, IEnumerable<string> keys)
        {
            var distinct = keys.Where(k => !string.IsNullOrWhiteSpace(k))
                               .Select(k => k.Trim().ToLowerInvariant())
                               .Distinct()
                               .ToList();
            if (distinct.Count == 0) return;

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            var names = string.Join(",", distinct.Select((_, i) => "@k" + i));
            cmd.CommandText = string.Format(DatabaseConstants.SqlRefreshMaterialAggregatesTemplate, names);
            for (var i = 0; i < distinct.Count; i++) cmd.Parameters.AddWithValue("@k" + i, distinct[i]);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>Refreshes every material a PR touches, before and after a write.
        ///
        /// <paramref name="keysBefore"/> comes from <see cref="KeysForPrAsync"/> called ahead of the
        /// write; the keys the PR names afterwards are read here. Passing only one of the two leaves
        /// the other side stale.</summary>
        public static async Task RefreshForPrAsync(SqliteConnection connection, SqliteTransaction? tx, Guid prId, IEnumerable<string>? keysBefore = null)
        {
            var keys = new List<string>();
            if (keysBefore != null) keys.AddRange(keysBefore);
            keys.AddRange(await KeysForPrAsync(connection, tx, prId).ConfigureAwait(false));
            await RefreshKeysAsync(connection, tx, keys).ConfigureAwait(false);
        }

        /// <summary>Rebuilds the whole table. Used by the migration and by the restructure operations,
        /// which move POs between several PRs at once - the same call the SearchBlob rebuild makes
        /// there, and for the same reason: enumerating exactly which materials moved is the kind of
        /// list that goes stale silently.</summary>
        public static async Task RebuildAllAsync(SqliteConnection connection, SqliteTransaction? tx = null)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = DatabaseConstants.SqlRebuildAllMaterialAggregates;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
