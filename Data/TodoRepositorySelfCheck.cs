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

                back.Title = marker + "-edited";
                back.IsDone = false;
                back.CompletedAt = null;
                back.RecurrenceRule = "Weekly";
                back.LinkedEntityType = "PR";
                back.LinkedEntityId = Guid.NewGuid();
                back.LinkedEntityLabel = "PR-0001";
                await repo.UpsertAsync(back);
                back = (await repo.GetAllAsync()).First(t => t.Id == id);
                Assert(back.Title == marker + "-edited" && !back.IsDone, "edit round trip");
                Assert(back.RecurrenceRule == "Weekly", "recurrence round trip");
                Assert(back.LinkedEntityLabel == "PR-0001" && back.LinkedEntityId != null, "link round trip");

                await repo.DeleteAsync(id);
                Assert((await repo.GetAllAsync()).All(t => t.Id != id), "DeleteAsync removes the task");

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

        private static void Report(string result) => Debug.WriteLine("TodoRepositorySelfCheck: " + result);
    }
}
