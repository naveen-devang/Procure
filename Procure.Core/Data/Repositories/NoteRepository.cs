using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;

namespace Procure.Data.Repositories
{
    // Same shape as TodoRepository, with one deliberate difference: bodies are never bulk-loaded.
    // GetListAsync omits Body; GetAsync fetches one row on demand.
    public class NoteRepository : INoteRepository
    {
        private readonly SqliteDatabase _db;

        public NoteRepository(SqliteDatabase db) => _db = db;

        public async Task<List<NoteListItem>> GetListAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<NoteListItem>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT Id, Title, Snippet, Pinned, SortOrder, UpdatedAt FROM Note ORDER BY Pinned DESC, SortOrder, UpdatedAt DESC;";

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new NoteListItem
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Title = reader.GetString(1),
                    Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Pinned = reader.GetInt32(3) != 0,
                    SortOrder = reader.GetInt32(4),
                    UpdatedAt = ParseDateTime(reader, 5) ?? DateTime.UtcNow,
                });
            }

            return list;
        }

        public async Task<Note?> GetAsync(Guid id)
        {
            await _db.InitializeAsync().ConfigureAwait(false);

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT Id, Title, Body, Format, Snippet, Pinned, SortOrder, CreatedAt, UpdatedAt FROM Note WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false)) return null;

            var note = new Note
            {
                Id = Guid.Parse(reader.GetString(0)),
                Title = reader.GetString(1),
                Body = reader.GetString(2),
                Format = reader.GetString(3),
                Snippet = reader.IsDBNull(4) ? null : reader.GetString(4),
                Pinned = reader.GetInt32(5) != 0,
                SortOrder = reader.GetInt32(6),
                CreatedAt = ParseDateTime(reader, 7) ?? DateTime.UtcNow,
                UpdatedAt = ParseDateTime(reader, 8) ?? DateTime.UtcNow,
            };
            reader.Close();

            using (var linkCmd = connection.CreateCommand())
            {
                linkCmd.CommandText = "SELECT EntityType, EntityId, EntityLabel FROM NoteLink WHERE NoteId = @Id;";
                linkCmd.Parameters.AddWithValue("@Id", id.ToString());
                using var lr = await linkCmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await lr.ReadAsync().ConfigureAwait(false))
                    note.Links.Add(new NoteLink
                    {
                        EntityType = lr.GetString(0),
                        EntityId = Guid.Parse(lr.GetString(1)),
                        Label = lr.IsDBNull(2) ? string.Empty : lr.GetString(2),
                    });
            }

            return note;
        }

        public Task SetLinksAsync(Guid noteId, IReadOnlyList<NoteLink> links) => Task.Run(async () =>
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await ReplaceLinksAsync(connection, noteId, links).ConfigureAwait(false);
        });

        private static async Task ReplaceLinksAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Guid noteId, System.Collections.Generic.IEnumerable<NoteLink> links)
        {
            using var tx = connection.BeginTransaction();

            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM NoteLink WHERE NoteId = @N;";
                del.Parameters.AddWithValue("@N", noteId.ToString());
                await del.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            foreach (var link in links)
            {
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText =
                    "INSERT OR IGNORE INTO NoteLink (NoteId, EntityType, EntityId, EntityLabel) VALUES (@N, @Ty, @E, @L);";
                ins.Parameters.AddWithValue("@N", noteId.ToString());
                ins.Parameters.AddWithValue("@Ty", link.EntityType);
                ins.Parameters.AddWithValue("@E", link.EntityId.ToString());
                ins.Parameters.AddWithValue("@L", (object?)link.Label ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            tx.Commit();
        }

        public Task UpsertAsync(Note note, string plainText) => Task.Run(() => UpsertCoreAsync(note, plainText));

        private async Task UpsertCoreAsync(Note note, string plainText)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
INSERT INTO Note (Id, Title, Body, Format, Snippet, Pinned, SortOrder, CreatedAt, UpdatedAt)
VALUES (@Id, @Title, @Body, @Format, @Snippet, @Pinned, @SortOrder, @CreatedAt, @UpdatedAt)
ON CONFLICT(Id) DO UPDATE SET
    Title = excluded.Title,
    Body = excluded.Body,
    Format = excluded.Format,
    Snippet = excluded.Snippet,
    Pinned = excluded.Pinned,
    SortOrder = excluded.SortOrder,
    UpdatedAt = excluded.UpdatedAt;";

            cmd.Parameters.AddWithValue("@Id", note.Id.ToString());
            cmd.Parameters.AddWithValue("@Title", note.Title ?? string.Empty);
            cmd.Parameters.AddWithValue("@Body", note.Body ?? string.Empty);
            cmd.Parameters.AddWithValue("@Format", string.IsNullOrEmpty(note.Format) ? "rtf" : note.Format);
            cmd.Parameters.AddWithValue("@Snippet", (object?)Note.BuildSnippet(plainText) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Pinned", note.Pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("@SortOrder", note.SortOrder);
            cmd.Parameters.AddWithValue("@CreatedAt", (note.CreatedAt == default ? DateTime.UtcNow : note.CreatedAt).ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            // Row exists now; (re)write its links. Snapshot - the collection lives on the UI thread.
            await ReplaceLinksAsync(connection, note.Id, note.Links.ToArray()).ConfigureAwait(false);
        }

        public Task SetTitleAsync(Guid id, string title) => Task.Run(async () =>
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Note SET Title = @Title, UpdatedAt = @UpdatedAt WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Title", title ?? string.Empty);
            cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@Id", id.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        });

        public Task SetPinnedAsync(Guid id, bool pinned) => Task.Run(async () =>
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Note SET Pinned = @Pinned WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Pinned", pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("@Id", id.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        });

        public Task DeleteAsync(Guid id) => Task.Run(async () =>
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Note WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        });

        public Task ReorderAsync(IReadOnlyList<(Guid Id, int SortOrder)> rows) => Task.Run(async () =>
        {
            if (rows.Count == 0) return;
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Note SET SortOrder = @SortOrder WHERE Id = @Id;";
            var pOrder = cmd.Parameters.Add("@SortOrder", SqliteType.Integer);
            var pId = cmd.Parameters.Add("@Id", SqliteType.Text);
            foreach (var (id, sortOrder) in rows)
            {
                pOrder.Value = sortOrder;
                pId.Value = id.ToString();
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            tx.Commit();
        });

        private static DateTime? ParseDateTime(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null
                : DateTime.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }
}
