using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Utilities;

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

                // Reminders: the open task is listed with its link; a snooze sticks through an edit that
                // leaves the due date alone, and a new due date ends it; acknowledging is kept; "off"
                // takes the task out of the list.
                async Task<ReminderRow> Reminder() => (await repo.GetRemindersAsync()).First(r => r.Id == id);
                back = (await repo.GetAllAsync()).First(t => t.Id == id);
                var r0 = await Reminder();
                Assert(r0.ReminderTime is null && r0.LinkLabel == "PR-0001" && r0.DueDate == DateTime.Today.AddDays(2), "reminder row round trip");
                var snooze = DateTime.Now.AddMinutes(10);
                await repo.SnoozeReminderAsync(id, snooze);
                Assert((await Reminder()).SnoozedUntil == snooze, "snooze round trip");
                back.Notes = "edited";
                await repo.UpsertAsync(back);
                Assert((await Reminder()).SnoozedUntil == snooze, "an edit that keeps the due date keeps the snooze");
                back.ReminderTime = "14:30";
                await repo.UpsertAsync(back);
                Assert((await Reminder()).SnoozedUntil is null && (await Reminder()).ReminderTime == "14:30", "a new reminder time ends the snooze");
                var fired = DateTime.Today.AddDays(2).AddHours(14.5);
                await repo.AcknowledgeReminderAsync(id, fired);
                Assert((await Reminder()).Acknowledged == fired, "acknowledged round trip");
                back.ReminderTime = TaskReminders.Off;
                await repo.UpsertAsync(back);
                Assert((await repo.GetRemindersAsync()).All(r => r.Id != id), "reminders off leaves the list");
                Assert((await repo.GetRemindersAsync()).All(r => r.Id != childId), "a task with no due date is never reminded");

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
