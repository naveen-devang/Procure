using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;

namespace Procure.Data
{
    /// <summary>
    /// The one runnable check behind the task list: it proves a task survives the full round trip
    /// through the real repository - create, read back, toggle done, edit, delete - with the SQL,
    /// the UPSERT, and the date parse all exercised. Money path here is the persistence, not the
    /// in-memory grouping (that is plain LINQ and shows on screen the instant the page opens).
    ///
    /// Run it by launching a Debug build with PROCURE_TODO_SELFCHECK=1 set. It writes to the live
    /// database and removes the task it created, so it is Debug only and never runs unless asked.
    /// </summary>
    internal static class TodoRepositorySelfCheck
    {
        public static async Task RunAsync(ITodoRepository repo)
        {
            var marker = "todo-selfcheck-" + Guid.NewGuid().ToString("N")[..8];
            var id = Guid.NewGuid();

            try
            {
                var task = new TodoTask
                {
                    Id = id,
                    Title = marker,
                    Priority = TodoPriority.High,
                    DueDate = DateTime.Today.AddDays(2),
                    Notes = "note",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                await repo.UpsertAsync(task);

                var back = (await repo.GetAllAsync()).FirstOrDefault(t => t.Id == id)
                           ?? throw new InvalidOperationException("task not found after UpsertAsync");
                Assert(back.Title == marker, "title round trip");
                Assert(back.Priority == TodoPriority.High, "priority round trip");
                Assert(back.DueDate?.Date == DateTime.Today.AddDays(2), "due date round trip");
                Assert(back.Notes == "note", "notes round trip");
                Assert(!back.IsDone, "starts not done");

                await repo.SetDoneAsync(id, true, DateTime.UtcNow);
                back = (await repo.GetAllAsync()).First(t => t.Id == id);
                Assert(back.IsDone && back.CompletedAt != null, "SetDoneAsync marks done with a timestamp");

                var linkA = Guid.NewGuid();
                var linkB = Guid.NewGuid();
                back.Title = marker + "-edited";
                back.IsDone = false;
                back.CompletedAt = null;
                back.RecurrenceRule = "Weekly";
                back.Links.Add(new TaskLink { EntityType = "PR", EntityId = linkA, Label = "PR-0001" });
                back.Links.Add(new TaskLink { EntityType = "RFQ", EntityId = linkB, Label = "RFQ-0007" });
                await repo.UpsertAsync(back);
                back = (await repo.GetAllAsync()).First(t => t.Id == id);
                Assert(back.Title == marker + "-edited" && !back.IsDone, "edit round trip");
                Assert(back.RecurrenceRule == "Weekly", "recurrence round trip");
                Assert(back.Links.Count == 2
                       && back.Links.Any(l => l.EntityId == linkA && l.Label == "PR-0001")
                       && back.Links.Any(l => l.EntityId == linkB && l.EntityType == "RFQ"), "multi-link round trip");

                // Targeted link write drops one, keeps the other.
                back.Links.Remove(back.Links.First(l => l.EntityId == linkB));
                await repo.SetLinksAsync(back.Id, back.Links.ToList());
                back = (await repo.GetAllAsync()).First(t => t.Id == id);
                Assert(back.Links.Count == 1 && back.Links[0].EntityId == linkA, "SetLinksAsync replaces the link set");

                // Sub-task + reverse-link lookup.
                var childId = Guid.NewGuid();
                await repo.UpsertAsync(new TodoTask
                {
                    Id = childId, ParentId = id, Title = marker + "-child",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                var child = (await repo.GetAllAsync()).First(t => t.Id == childId);
                Assert(child.ParentId == id, "sub-task ParentId round trip");

                var linked = await repo.GetLinkedAsync(linkA);
                Assert(linked.Any(t => t.Id == id) && linked.All(t => t.ParentId is null), "GetLinkedAsync returns the parent, not sub-tasks");

                await repo.DeleteAsync(id);
                var after = await repo.GetAllAsync();
                Assert(after.All(t => t.Id != id), "DeleteAsync removes the task");
                Assert(after.All(t => t.Id != childId), "deleting a parent cascades to sub-tasks");

                Report("PASS");
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message);
                try { await repo.DeleteAsync(id); } catch { /* best effort cleanup */ }
                throw;
            }
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("failed: " + what);
        }

        private static void Report(string result) => SelfCheckLog.Write("TodoRepositorySelfCheck: " + result);
    }
}
