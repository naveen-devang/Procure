using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.PageModels
{
    // The reverse side of task linking: the "Tasks (n)" strip on an expanded PR detail panel.
    // Loaded on demand when the panel is built - no task data travels with the PR otherwise.
    public partial class PrListPageModel
    {
        public async Task LoadLinkedTasksAsync(PurchaseRequisition? pr, bool force = false)
        {
            if (pr is null || (pr.LinkedTasksLoaded && !force)) return;
            try
            {
                var tasks = await _todoRepo.GetLinkedAsync(pr.Id);
                pr.LinkedTasks = new ObservableCollection<TodoTask>(tasks);
                pr.LinkedTasksLoaded = true;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public async Task AddLinkedTaskAsync(PurchaseRequisition? pr, string? title)
        {
            title = title?.Trim();
            if (pr is null || string.IsNullOrEmpty(title)) return;

            var task = new TodoTask
            {
                Title = title,
                DueDate = DateTime.Today,   // every task carries a due date; default to today
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            task.Links.Add(new TaskLink { EntityType = "PR", EntityId = pr.Id, Label = pr.PrNo });
            try
            {
                await _todoRepo.UpsertAsync(task);
                pr.LinkedTasks.Add(task);
                pr.RefreshLinkedTaskCount();
                Utilities.TodoChangeNotifier.NotifyChanged();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public async Task ToggleLinkedTaskAsync(TodoTask? task)
        {
            if (task is null) return;
            var done = !task.IsDone;
            task.IsDone = done;
            task.CompletedAt = done ? DateTime.UtcNow : null;
            try
            {
                await _todoRepo.SetDoneAsync(task.Id, done, task.CompletedAt);
                Utilities.TodoChangeNotifier.NotifyChanged();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public async Task DeleteLinkedTaskAsync(PurchaseRequisition? pr, TodoTask? task)
        {
            if (pr is null || task is null) return;

            var name = string.IsNullOrWhiteSpace(task.Title) ? "this task" : $"“{task.Title}”";
            var ok = await _dialogs.DisplayAlertAsync("Delete task",
                $"Are you sure you want to delete {name}?\n\nYou can undo this for 10 seconds.", "Delete", "Cancel");
            if (!ok) return;

            try
            {
                if (_undo is null)
                {
                    await _todoRepo.DeleteAsync(task.Id);
                }
                else
                {
                    var prId = pr.Id;
                    await _undo.StartAsync("Task deleted", new[] { new PendingDeleteItem(DeleteKind.Task, task.Id) },
                        onUndo: () => _ = RestoreStripAsync());

                    // Whichever copy of the PR the board holds by then; the strip re-reads its tasks.
                    async Task RestoreStripAsync()
                    {
                        var current = _loadedPrs.FirstOrDefault(p => p.Id == prId) ?? pr;
                        await LoadLinkedTasksAsync(current, force: true);
                        current.RefreshLinkedTaskCount();
                        Utilities.TodoChangeNotifier.NotifyChanged();
                    }
                }
                pr.LinkedTasks.Remove(task);
                pr.RefreshLinkedTaskCount();
                Utilities.TodoChangeNotifier.NotifyChanged();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }
    }
}
