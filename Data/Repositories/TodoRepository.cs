using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;

namespace Procure.Data.Repositories
{
    // Full-load-in-memory, same shape as CallOffRepository - a personal task list is a few
    // hundred rows at the absolute most, so there is no paging, no search blob, no
    // denormalisation. Writes run off the UI thread via Task.Run like the other repos.
    public class TodoRepository : ITodoRepository
    {
        private readonly SqliteDatabase _db;

        private const string DateFmt = "yyyy-MM-dd";
        private const string AllColumns =
            "Id, Title, Notes, Priority, IsDone, DueDate, CompletedAt, SortOrder, CreatedAt, UpdatedAt, " +
            "ParentId, RecurrenceRule, PlannedForDate";

        public TodoRepository(SqliteDatabase db)
        {
            _db = db;
        }

        public async Task<List<TodoTask>> GetAllAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<TodoTask>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            // Newest first as the stable base order; the page model re-sorts within each group.
            cmd.CommandText = $"SELECT {AllColumns} FROM TodoTask ORDER BY CreatedAt DESC;";

            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(ReadRow(reader));

            await LoadLinksAsync(connection, list).ConfigureAwait(false);
            return list;
        }

        public async Task<List<TodoTask>> GetLinkedAsync(Guid entityId)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<TodoTask>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"SELECT {AllColumns} FROM TodoTask
WHERE Id IN (SELECT TaskId FROM TodoTaskLink WHERE EntityId = @E) AND ParentId IS NULL
ORDER BY IsDone, CreatedAt DESC;";
            cmd.Parameters.AddWithValue("@E", entityId.ToString());

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(ReadRow(reader));

            return list;
        }

        // One query for every link row, matched back to the tasks already loaded.
        private static async Task LoadLinksAsync(SqliteConnection connection, List<TodoTask> tasks)
        {
            if (tasks.Count == 0) return;
            var byId = tasks.ToDictionary(t => t.Id);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT TaskId, EntityType, EntityId, EntityLabel FROM TodoTaskLink;";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (Guid.TryParse(reader.GetString(0), out var taskId) && byId.TryGetValue(taskId, out var task))
                    task.Links.Add(new TaskLink
                    {
                        EntityType = reader.GetString(1),
                        EntityId = Guid.Parse(reader.GetString(2)),
                        Label = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    });
            }
        }

        private static TodoTask ReadRow(SqliteDataReader reader) => new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            Title = reader.GetString(1),
            Notes = reader.IsDBNull(2) ? null : reader.GetString(2),
            Priority = (TodoPriority)reader.GetInt32(3),
            IsDone = reader.GetInt32(4) != 0,
            DueDate = ParseDate(reader, 5),
            CompletedAt = ParseDateTime(reader, 6),
            SortOrder = reader.GetInt32(7),
            CreatedAt = ParseDateTime(reader, 8) ?? DateTime.UtcNow,
            UpdatedAt = ParseDateTime(reader, 9) ?? DateTime.UtcNow,
            ParentId = reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
            RecurrenceRule = reader.IsDBNull(11) ? null : reader.GetString(11),
            PlannedForDate = ParseDate(reader, 12),
        };

        public Task UpsertAsync(TodoTask task) => Task.Run(() => UpsertCoreAsync(task));

        private async Task UpsertCoreAsync(TodoTask task)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO TodoTask (Id, Title, Notes, Priority, IsDone, DueDate, CompletedAt, SortOrder, CreatedAt, UpdatedAt,
                      ParentId, RecurrenceRule, PlannedForDate)
VALUES (@Id, @Title, @Notes, @Priority, @IsDone, @DueDate, @CompletedAt, @SortOrder, @CreatedAt, @UpdatedAt,
        @ParentId, @RecurrenceRule, @PlannedForDate)
ON CONFLICT(Id) DO UPDATE SET
    Title = excluded.Title,
    Notes = excluded.Notes,
    Priority = excluded.Priority,
    IsDone = excluded.IsDone,
    DueDate = excluded.DueDate,
    CompletedAt = excluded.CompletedAt,
    SortOrder = excluded.SortOrder,
    UpdatedAt = excluded.UpdatedAt,
    ParentId = excluded.ParentId,
    RecurrenceRule = excluded.RecurrenceRule,
    PlannedForDate = excluded.PlannedForDate;";

                cmd.Parameters.AddWithValue("@Id", task.Id.ToString());
                cmd.Parameters.AddWithValue("@Title", task.Title);
                cmd.Parameters.AddWithValue("@Notes", (object?)task.Notes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Priority", (int)task.Priority);
                cmd.Parameters.AddWithValue("@IsDone", task.IsDone ? 1 : 0);
                cmd.Parameters.AddWithValue("@DueDate", DateOrNull(task.DueDate));
                cmd.Parameters.AddWithValue("@CompletedAt", DateTimeOrNull(task.CompletedAt));
                cmd.Parameters.AddWithValue("@SortOrder", task.SortOrder);
                cmd.Parameters.AddWithValue("@CreatedAt", (task.CreatedAt == default ? DateTime.UtcNow : task.CreatedAt).ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@ParentId", (object?)task.ParentId?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RecurrenceRule", (object?)task.RecurrenceRule ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PlannedForDate", DateOrNull(task.PlannedForDate));

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Task row exists now; (re)write its link set (FK TaskId -> TodoTask). Snapshot the
            // collection - it lives on the UI thread.
            await ReplaceLinksAsync(connection, task.Id, task.Links.ToArray()).ConfigureAwait(false);
        }

        public Task SetLinksAsync(Guid taskId, IReadOnlyList<TaskLink> links) =>
            Task.Run(() => SetLinksCoreAsync(taskId, links));

        private async Task SetLinksCoreAsync(Guid taskId, IReadOnlyList<TaskLink> links)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await ReplaceLinksAsync(connection, taskId, links).ConfigureAwait(false);
        }

        private static async Task ReplaceLinksAsync(SqliteConnection connection, Guid taskId, IEnumerable<TaskLink> links)
        {
            using var tx = connection.BeginTransaction();

            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM TodoTaskLink WHERE TaskId = @T;";
                del.Parameters.AddWithValue("@T", taskId.ToString());
                await del.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            foreach (var link in links)
            {
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText =
                    "INSERT OR IGNORE INTO TodoTaskLink (TaskId, EntityType, EntityId, EntityLabel) VALUES (@T, @Ty, @E, @L);";
                ins.Parameters.AddWithValue("@T", taskId.ToString());
                ins.Parameters.AddWithValue("@Ty", link.EntityType);
                ins.Parameters.AddWithValue("@E", link.EntityId.ToString());
                ins.Parameters.AddWithValue("@L", (object?)link.Label ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            tx.Commit();
        }

        public Task SetDoneAsync(Guid id, bool done, DateTime? completedAt) =>
            Task.Run(() => SetDoneCoreAsync(id, done, completedAt));

        private async Task SetDoneCoreAsync(Guid id, bool done, DateTime? completedAt)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE TodoTask SET IsDone = @IsDone, CompletedAt = @CompletedAt, UpdatedAt = @UpdatedAt WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@IsDone", done ? 1 : 0);
            cmd.Parameters.AddWithValue("@CompletedAt", DateTimeOrNull(completedAt));
            cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@Id", id.ToString());

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public Task DeleteAsync(Guid id) => Task.Run(() => DeleteCoreAsync(id));

        private async Task DeleteCoreAsync(Guid id)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM TodoTask WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public Task DeleteCompletedAsync() => Task.Run(DeleteCompletedCoreAsync);

        private async Task DeleteCompletedCoreAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM TodoTask WHERE IsDone = 1;";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public Task ReorderAsync(IReadOnlyList<(Guid Id, int SortOrder)> rows) =>
            Task.Run(() => ReorderCoreAsync(rows));

        private async Task ReorderCoreAsync(IReadOnlyList<(Guid Id, int SortOrder)> rows)
        {
            if (rows.Count == 0) return;

            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE TodoTask SET SortOrder = @SortOrder WHERE Id = @Id;";
            var pOrder = cmd.Parameters.Add("@SortOrder", SqliteType.Integer);
            var pId = cmd.Parameters.Add("@Id", SqliteType.Text);

            foreach (var (id, sortOrder) in rows)
            {
                pOrder.Value = sortOrder;
                pId.Value = id.ToString();
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            tx.Commit();
        }

        public async Task<List<TaskLinkTarget>> GetLinkTargetsAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var list = new List<TaskLinkTarget>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT 'PR' AS T, Id, PrNo || ' — ' || COALESCE(NULLIF(Description,''), 'PR') AS L, CreatedAt AS Ord
FROM PurchaseRequisition WHERE ParentPrId IS NULL
UNION ALL
SELECT 'RFQ', r.Id, COALESCE(NULLIF(r.RfqNo,''), 'RFQ') || ' — ' || COALESCE(NULLIF(r.Vendor,''), 'vendor'),
       (SELECT CreatedAt FROM PurchaseRequisition WHERE Id = r.PrId)
FROM RequestForQuotation r
UNION ALL
SELECT 'PO', p.Id, COALESCE(NULLIF(p.PoNo,''), 'PO') || ' — ' || COALESCE(NULLIF(p.Vendor,''), 'vendor'),
       (SELECT CreatedAt FROM PurchaseRequisition WHERE Id = p.PrId)
FROM PurchaseOrder p
ORDER BY Ord DESC;";

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new TaskLinkTarget(
                    reader.GetString(0),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2)));
            }

            return list;
        }

        private static object DateOrNull(DateTime? d) =>
            d.HasValue ? d.Value.ToString(DateFmt, CultureInfo.InvariantCulture) : DBNull.Value;

        private static object DateTimeOrNull(DateTime? d) =>
            d.HasValue ? d.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value;

        private static DateTime? ParseDate(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null
                : DateTime.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d) ? d : null;

        private static DateTime? ParseDateTime(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null
                : DateTime.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }
}
