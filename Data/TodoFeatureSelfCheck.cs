using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.PageModels;
using Procure.Services;

namespace Procure.Data
{
    /// <summary>
    /// Drives TodoPageModel end to end against the live database - grouping, board, calendar,
    /// recurrence, sub-tasks, reorder, link typeahead - and checks for a memory leak across many
    /// rebuilds. Every task it creates carries a marker and is deleted at the end.
    ///
    /// Runs with PROCURE_TODO_SELFCHECK=1 (Debug only), after TodoRepositorySelfCheck.
    /// </summary>
    internal static class TodoFeatureSelfCheck
    {
        public static async Task RunAsync(ITodoRepository repo, IErrorHandler errorHandler, ILinkTargetService linkTargets)
        {
            var marker = "tfsc-" + Guid.NewGuid().ToString("N")[..6] + "-";
            var vm = new TodoPageModel(repo, errorHandler, linkTargets);

            try
            {
                await vm.LoadAsync(force: true);
                var baseline = CountAll(vm);

                // ---- quick-add captures priority + due + notes ----
                vm.NewTaskTitle = marker + "overdue";
                vm.NewTaskPriority = TodoPriority.High;
                vm.NewTaskDueDate = DateTime.Today.AddDays(-2);
                vm.NewTaskNotes = "n";
                await vm.QuickAddAsync();

                await Add(vm, marker + "today", TodoPriority.Medium, DateTime.Today);
                await Add(vm, marker + "week", TodoPriority.Low, DateTime.Today.AddDays(3));
                await Add(vm, marker + "later", TodoPriority.None, DateTime.Today.AddDays(20));
                await Add(vm, marker + "autotoday", TodoPriority.None, null);   // no date picked -> due today

                Assert(vm.NewTaskTitle == "" && vm.NewTaskDueDate is null, "composer resets after add");

                // Genuinely dateless tasks (detail-panel clear, legacy rows) still exist - insert straight.
                await repo.UpsertAsync(new TodoTask { Title = marker + "nodate-a", SortOrder = 1_000_000, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                await repo.UpsertAsync(new TodoTask { Title = marker + "nodate-b", SortOrder = 1_000_001, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                await vm.LoadAsync(force: true);

                // ---- date grouping ----
                vm.SetView("List");
                vm.GroupMode = "Date";
                await vm.RefreshAsync();
                AssertInGroup(vm, "Overdue", marker + "overdue");
                AssertInGroup(vm, "Today", marker + "today");
                AssertInGroup(vm, "Today", marker + "autotoday");   // composer with no date defaults to today
                AssertInGroup(vm, "This week", marker + "week");
                AssertInGroup(vm, "Later", marker + "later");
                AssertInGroup(vm, "No date", marker + "nodate-a");
                Assert(vm.OverdueCount >= 1 && vm.TodayCount >= 1, "overdue / today counts");

                // ---- priority grouping + manual reorder ----
                vm.GroupMode = "Priority";
                await vm.RefreshAsync();
                AssertInGroup(vm, "High priority", marker + "overdue");
                AssertInGroup(vm, "No priority", marker + "nodate-a");

                // Reorder only among this run's own tasks (they sort after every pre-existing
                // SortOrder=0 row, so they are contiguous at the tail of the group).
                var noneGroup = vm.Groups.First(g => g.Header == "No priority");
                var mineNone = noneGroup.Where(t => t.Title.StartsWith(marker)).ToList();
                Assert(mineNone.Count >= 2, "at least two own tasks in No priority");
                var a0 = mineNone[0].Title;
                var a1 = mineNone[1].Title;
                vm.Select(mineNone[0]);
                await vm.MoveSelectedAsync("Down");
                var mineNone2 = vm.Groups.First(g => g.Header == "No priority")
                    .Where(t => t.Title.StartsWith(marker)).ToList();
                Assert(mineNone2[0].Title == a1 && mineNone2[1].Title == a0, "move down swaps order");

                // ---- filter ----
                vm.FilterText = marker + "week";
                await vm.RefreshAsync();
                Assert(vm.Groups.SelectMany(g => g).Count(t => t.Title.StartsWith(marker)) == 1, "filter narrows to one");
                vm.FilterText = "";
                await vm.RefreshAsync();

                // ---- board ----
                vm.SetView("Board");
                Assert(vm.BoardHigh.Any(t => t.Title == marker + "overdue"), "board High column");
                Assert(vm.BoardNone.Count(t => t.Title.StartsWith(marker + "nodate")) == 2, "board None column");

                // ---- calendar: week ----
                vm.SetView("Calendar");
                vm.CalendarToday();
                Assert(vm.WeekColumns.Count == 7, "week has 7 columns");
                for (var i = 0; i < 7; i++) Assert(vm.WeekColumns[i].ColIndex == i, "week column index");
                Assert(vm.WeekColumns[0].Date == vm.WeekStart, "week starts on WeekStart");
                Assert(vm.WeekColumns.Any(c => c.Tasks.Any(t => t.Title == marker + "today")), "today's task lands in a week column");
                vm.WeekNext();
                Assert(vm.WeekColumns.Count == 7 && vm.WeekColumns[0].Date == vm.WeekStart, "week nav keeps 7 columns");
                vm.WeekPrev();

                // ---- sub-tasks ----
                vm.SetView("List");
                vm.GroupMode = "Date";
                await vm.RefreshAsync();
                var parent = Find(vm, marker + "later");
                vm.Select(parent);
                vm.NewSubtaskTitle = marker + "sub1";
                await vm.AddSubtaskAsync();
                vm.NewSubtaskTitle = marker + "sub2";
                await vm.AddSubtaskAsync();
                Assert(vm.SelectedSubtasks.Count == 2, "two sub-tasks added");
                Assert(parent.SubtaskBadge == "0/2", "parent badge 0/2");
                await vm.ToggleSubtaskAsync(vm.SelectedSubtasks[0]);
                Assert(parent.SubtaskBadge == "1/2", "parent badge 1/2 after one done");

                await vm.RefreshAsync();
                Assert(!vm.Groups.SelectMany(g => g).Any(t => t.Title == marker + "sub1"), "sub-tasks never appear in the main list");
                var parentAgain = Find(vm, marker + "later");
                Assert(parentAgain.SubtaskBadge == "1/2", "badge survives reload");

                // ---- recurrence ----
                var rec = Find(vm, marker + "today");
                vm.Select(rec);
                rec.RecurrenceRule = "Weekly";
                await vm.ToggleDoneAsync(rec);              // completing a repeating task spawns the next
                await vm.RefreshAsync();
                var spawned = vm.Groups.SelectMany(g => g)
                    .FirstOrDefault(t => t.Title == marker + "today" && !t.IsDone && t.DueDate?.Date == DateTime.Today.AddDays(7));
                Assert(spawned != null, "recurrence spawned the next occurrence 7 days out");

                // ---- link typeahead ----
                vm.Select(Find(vm, marker + "week"));
                vm.LinkQuery = "P";
                Assert(!vm.ShowLinkResults, "1-char query shows no results");
                vm.LinkQuery = "PR";
                if (vm.LinkResults.Count > 0)
                {
                    var target = vm.LinkResults[0];
                    await vm.PickLinkTargetAsync(target);
                    Assert(vm.SelectedTask!.HasLinks && vm.SelectedTask.Links.Any(l => l.Label == target.ChipLabel), "pick link adds to the task");
                    Assert(vm.LinkQuery == "" && !vm.ShowLinkResults, "picking clears the query");

                    await vm.PickLinkTargetAsync(target);
                    Assert(vm.SelectedTask.Links.Count == 1, "picking the same target twice does not duplicate");

                    if (vm.LinkResults.Count > 1)
                    {
                        await vm.PickLinkTargetAsync(vm.LinkResults[1]);
                        Assert(vm.SelectedTask.Links.Count == 2, "a task can hold multiple links");
                    }

                    var first = vm.SelectedTask.Links[0];
                    await vm.RemoveLinkAsync(first);
                    Assert(vm.SelectedTask.Links.All(l => l.EntityId != first.EntityId), "remove link drops just that one");
                }
                else
                {
                    SelfCheckLog.Write("TodoFeatureSelfCheck: (no PRs in DB - link typeahead pick/unlink skipped)");
                }

                // ---- finished view + clear ----
                vm.SetView("Finished");
                Assert(vm.Groups.SelectMany(g => g).Any(t => t.Title == marker + "today" && t.IsDone), "completed task is in Finished");

                // ---- memory: many rebuilds must not grow the heap unbounded ----
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var before = GC.GetTotalMemory(true);
                for (var i = 0; i < 400; i++)
                {
                    vm.SetView(i % 2 == 0 ? "List" : "Board");
                    vm.GroupMode = i % 2 == 0 ? "Priority" : "Date";
                    vm.FilterText = i % 3 == 0 ? marker : "";
                }
                vm.SetView("List");
                vm.FilterText = "";
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var after = GC.GetTotalMemory(true);
                var grewKb = (after - before) / 1024;
                SelfCheckLog.Write($"TodoFeatureSelfCheck: heap delta over 400 rebuilds = {grewKb} KB");
                Assert(grewKb < 4096, $"no runaway growth over 400 rebuilds (grew {grewKb} KB)");

                Report("PASS");
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message);
                throw;
            }
            finally
            {
                try
                {
                    // Query the repo directly - the VM projection only holds the current view.
                    var all = await repo.GetAllAsync();
                    var mine = all.Where(t => t.Title.StartsWith(marker)).ToList();
                    foreach (var t in mine.Where(t => t.ParentId is not null)) await repo.DeleteAsync(t.Id);
                    foreach (var t in mine.Where(t => t.ParentId is null)) await repo.DeleteAsync(t.Id);
                }
                catch { /* best-effort cleanup */ }
                vm.UnsubscribeForTest();
            }
        }

        private static async Task Add(TodoPageModel vm, string title, TodoPriority p, DateTime? due)
        {
            vm.NewTaskTitle = title;
            vm.NewTaskPriority = p;
            vm.NewTaskDueDate = due;
            await vm.QuickAddAsync();
        }

        private static System.Collections.Generic.IEnumerable<TodoTask> AllTasks(TodoPageModel vm) =>
            vm.Groups.SelectMany(g => g)
              .Concat(vm.BoardHigh).Concat(vm.BoardMedium).Concat(vm.BoardLow).Concat(vm.BoardNone)
              .Concat(vm.SelectedSubtasks)
              .Distinct();

        private static int CountAll(TodoPageModel vm) => AllTasks(vm).Count();

        private static TodoTask Find(TodoPageModel vm, string title) =>
            vm.Groups.SelectMany(g => g).FirstOrDefault(t => t.Title == title)
            ?? throw new InvalidOperationException($"task not found in current view: {title}");

        private static void AssertInGroup(TodoPageModel vm, string header, string title)
        {
            var g = vm.Groups.FirstOrDefault(x => x.Header == header)
                    ?? throw new InvalidOperationException($"group missing: {header}");
            if (g.All(t => t.Title != title))
                throw new InvalidOperationException($"'{title}' not in group '{header}'");
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("failed: " + what);
        }

        private static void Report(string result) => SelfCheckLog.Write("TodoFeatureSelfCheck: " + result);
    }
}
