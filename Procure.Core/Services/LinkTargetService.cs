using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data;
using Procure.Models;

namespace Procure.Services
{
    /// <summary>
    /// The one place that answers "which PRs, RFQs and POs can this be linked to".
    ///
    /// Tasks and Notes each used to hold their own full <c>List&lt;TaskLinkTarget&gt;</c> of every
    /// linkable entity, loaded on page load and kept for the app's lifetime - the same rows
    /// materialised twice, unbounded, for a picker that never shows more than a dozen at once. On a
    /// 20,000-PR database that measured ~48MB per copy. Both queries are bounded now: the picker
    /// searches with a LIMIT, and existing links resolve their labels by id.
    /// </summary>
    public interface ILinkTargetService
    {
        /// <summary>Link targets whose label matches <paramref name="term"/>, newest first.</summary>
        Task<List<TaskLinkTarget>> SearchAsync(string term, int limit = 12);

        /// <summary>Current chip labels for ids already linked. An id with no row (its entity was
        /// deleted) is simply absent from the result.</summary>
        Task<Dictionary<Guid, string>> GetChipLabelsAsync(IReadOnlyCollection<Guid> ids);
    }

    public sealed class LinkTargetService : ILinkTargetService
    {
        private readonly SqliteDatabase _db;

        public LinkTargetService(SqliteDatabase db) => _db = db;

        public async Task<List<TaskLinkTarget>> SearchAsync(string term, int limit = 12)
        {
            var list = new List<TaskLinkTarget>();
            if (string.IsNullOrWhiteSpace(term)) return list;

            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = DatabaseConstants.SqlLinkTargetSearch;
            // The typed text is a substring to find, not a pattern to honour - escape LIKE's own
            // wildcards so a PR number containing "_" doesn't match any character in that slot.
            var escaped = term.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            cmd.Parameters.AddWithValue("@q", "%" + escaped + "%");
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                list.Add(new TaskLinkTarget(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2)));

            return list;
        }

        public async Task<Dictionary<Guid, string>> GetChipLabelsAsync(IReadOnlyCollection<Guid> ids)
        {
            var map = new Dictionary<Guid, string>();
            if (ids.Count == 0) return map;

            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var distinct = ids.Distinct().ToList();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = string.Format(
                DatabaseConstants.SqlLinkTargetsByIdsTemplate,
                string.Join(",", distinct.Select((_, i) => "@id" + i)));
            for (var i = 0; i < distinct.Count; i++)
                cmd.Parameters.AddWithValue("@id" + i, distinct[i].ToString());

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var target = new TaskLinkTarget(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2));
                map[target.Id] = target.ChipLabel;
            }

            return map;
        }
    }
}
