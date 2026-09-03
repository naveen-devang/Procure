using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;

namespace Procure.PageModels
{
    // Native list + detail, with a Board tab (priority columns) and a Finished tab. The whole
    // task list is loaded once into _all and kept there - a personal checklist is hundreds of
    // rows at most, so every view is a cheap in-memory projection rebuilt on demand, and only
    // the projection for the view actually on screen is ever rebuilt.
    public partial class TodoPageModel : ObservableObject
    {
        private readonly ITodoRepository _repo;
        private readonly IErrorHandler _errorHandler;

        private List<TodoTask> _all = new();
        private bool _loaded;

        // PR/RFQ/PO link targets, loaded alongside the tasks. _linkChip maps a target id to its
        // short chip label so a renamed target still shows correctly after a reload.
        private readonly Dictionary<Guid, string> _linkChip = new();
        public ObservableCollection<TaskLinkTarget> LinkTargets { get; } = new();

        public string[] RecurrenceOptions { get; } = { "None", "Daily", "Weekly", "Monthly" };

        public bool IsVisible { get; set; }

        // "List" | "Board" | "Finished" - string rather than an enum so the XAML view toggle can
        // compare it with StringEqualsConverter directly.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsListView))]
        [NotifyPropertyChangedFor(nameof(IsBoardView))]
        [NotifyPropertyChangedFor(nameof(IsFinishedView))]
        [NotifyPropertyChangedFor(nameof(ShowQuickAdd))]
        public partial string CurrentView { get; set; } = "List";

        // "Date" | "Priority"
        [ObservableProperty]
        public partial string GroupMode { get; set; } = "Date";

        // List and Finished views bind this (grouped CollectionView). Board binds the four
        // BoardColumn* properties, which are always non-null so the XAML never touches a missing
        // index.
        [ObservableProperty]
        public partial ObservableCollection<TodoTaskGroup> Groups { get; set; } = new();

        [ObservableProperty] public partial TodoTaskGroup BoardHigh { get; set; } = new("High priority", System.Array.Empty<TodoTask>());
        [ObservableProperty] public partial TodoTaskGroup BoardMedium { get; set; } = new("Medium priority", System.Array.Empty<TodoTask>());
        [ObservableProperty] public partial TodoTaskGroup BoardLow { get; set; } = new("Low priority", System.Array.Empty<TodoTask>());
        [ObservableProperty] public partial TodoTaskGroup BoardNone { get; set; } = new("No priority", System.Array.Empty<TodoTask>());

        [ObservableProperty]
        public partial string FilterText { get; set; } = string.Empty;

        // ---- quick-add composer: title + priority + due date + notes, all captured before the
        // task is created ----
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAddTask))]
        public partial string NewTaskTitle { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewTaskNotes { get; set; } = string.Empty;

        [ObservableProperty]
        public partial TodoPriority NewTaskPriority { get; set; } = TodoPriority.None;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NewHasDueDate))]
        [NotifyPropertyChangedFor(nameof(NewDueLabel))]
        [NotifyPropertyChangedFor(nameof(NewDuePickerDate))]
        public partial DateTime? NewTaskDueDate { get; set; }

        [ObservableProperty]
        public partial bool ComposerOpen { get; set; }

        public bool CanAddTask => !string.IsNullOrWhiteSpace(NewTaskTitle);
        public bool NewHasDueDate => NewTaskDueDate is not null;
        public string NewDueLabel => NewTaskDueDate is { } d ? d.ToString("ddd, d MMM") : "No due date";

        public DateTime NewDuePickerDate
        {
            get => NewTaskDueDate ?? DateTime.Today;
            set => NewTaskDueDate = value.Date;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        [NotifyPropertyChangedFor(nameof(SelectedHasDueDate))]
        [NotifyPropertyChangedFor(nameof(SelectedDueDate))]
        [NotifyPropertyChangedFor(nameof(SelectedLinkTarget))]
        [NotifyPropertyChangedFor(nameof(SelectedRecurrence))]
        public partial TodoTask? SelectedTask { get; set; }

        [ObservableProperty]
        public partial int OpenCount { get; set; }

        [ObservableProperty]
        public partial int TodayCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOverdue))]
        public partial int OverdueCount { get; set; }

        public bool HasOverdue => OverdueCount > 0;

        // Picker bridges for the detail panel.
        public TaskLinkTarget? SelectedLinkTarget
        {
            get => SelectedTask?.LinkedEntityId is { } id ? LinkTargets.FirstOrDefault(t => t.Id == id) : null;
            set
            {
                if (SelectedTask is null) return;
                SelectedTask.LinkedEntityType = value?.Type;
                SelectedTask.LinkedEntityId = value?.Id;
                SelectedTask.LinkedEntityLabel = value?.ChipLabel;
                OnPropertyChanged(nameof(SelectedLinkTarget));
            }
        }

        public string SelectedRecurrence
        {
            get => string.IsNullOrEmpty(SelectedTask?.RecurrenceRule) ? "None" : SelectedTask!.RecurrenceRule!;
            set
            {
                if (SelectedTask is null) return;
                SelectedTask.RecurrenceRule = value == "None" ? null : value;
            }
        }

        public bool HasSelection => SelectedTask is not null;
        public bool SelectedHasDueDate => SelectedTask?.DueDate is not null;

        // DatePicker.Date is a non-nullable DateTime; this bridges it to the nullable model field.
        // Picking any date sets it; the Clear button nulls it back out.
        public DateTime SelectedDueDate
        {
            get => SelectedTask?.DueDate ?? DateTime.Today;
            set
            {
                if (SelectedTask is null || SelectedTask.DueDate == value.Date) return;
                SelectedTask.DueDate = value.Date;
                OnPropertyChanged(nameof(SelectedHasDueDate));
            }
        }

        public bool IsListView => CurrentView == "List";
        public bool IsBoardView => CurrentView == "Board";
        public bool IsFinishedView => CurrentView == "Finished";
        public bool ShowQuickAdd => CurrentView != "Finished";

        public Array PriorityOptions { get; } = Enum.GetValues(typeof(TodoPriority));

        public TodoPageModel(ITodoRepository repo, IErrorHandler errorHandler)
        {
            _repo = repo;
            _errorHandler = errorHandler;
        }

        // Warmed from AppShell like the PR board: fills _all before the first visit so opening
        // the tab is instant. Safe to call repeatedly.
        public Task PreloadDataAsync() => LoadAsync();

        [RelayCommand]
        public Task RefreshAsync() => LoadAsync(force: true);

        public async Task LoadAsync(bool force = false)
        {
            if (_loaded && !force) return;

            var selectedId = SelectedTask?.Id;
            try
            {
                var loaded = await _repo.GetAllAsync();
                await LoadLinkTargetsAsync();
                foreach (var t in loaded)
                {
                    HookTask(t);
                    ResolveLinkLabel(t);
                }
                _all = loaded;
                _loaded = true;
                Rebuild();

                if (selectedId is { } id)
                {
                    var again = _all.FirstOrDefault(t => t.Id == id);
                    if (again != null) Select(again);
                    else SelectedTask = null;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ---- filter debounce, matching CallOffPageModel ----
        private int _rebuildGeneration;

        partial void OnFilterTextChanged(string value)
        {
            var generation = ++_rebuildGeneration;
            Dispatcher.GetForCurrentThread()?.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
            {
                if (generation == _rebuildGeneration) Rebuild();
            });
        }

        partial void OnGroupModeChanged(string value) => Rebuild();
        partial void OnCurrentViewChanged(string value) => Rebuild();

        // ---- projection ----
        private IEnumerable<TodoTask> Filtered(IEnumerable<TodoTask> source)
        {
            var term = FilterText?.Trim();
            if (string.IsNullOrEmpty(term)) return source;
            return source.Where(t =>
                t.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (t.Notes?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        private async Task LoadLinkTargetsAsync()
        {
            try
            {
                var targets = await _repo.GetLinkTargetsAsync();
                LinkTargets.Clear();
                _linkChip.Clear();
                foreach (var t in targets)
                {
                    LinkTargets.Add(t);
                    _linkChip[t.Id] = t.ChipLabel;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        private void ResolveLinkLabel(TodoTask t)
        {
            if (t.LinkedEntityId is { } id && _linkChip.TryGetValue(id, out var chip))
                t.LinkedEntityLabel = chip;
        }

        private void Rebuild()
        {
            OpenCount = _all.Count(t => !t.IsDone);
            TodayCount = _all.Count(t => !t.IsDone && t.DueDate is { } d && d.Date == DateTime.Today);
            OverdueCount = _all.Count(t => t.IsOverdue);

            if (CurrentView == "Board")
            {
                BuildBoard();
                Groups = new ObservableCollection<TodoTaskGroup>();
            }
            else
            {
                Groups = CurrentView == "Finished" ? BuildFinished() : BuildList();
            }
        }

        private ObservableCollection<TodoTaskGroup> BuildList()
        {
            var open = Filtered(_all.Where(t => !t.IsDone)).ToList();
            var groups = new List<TodoTaskGroup>();

            if (GroupMode == "Priority")
            {
                // Priority mode is the "manual order" mode: SortOrder leads, so move up/down does
                // something visible regardless of due dates.
                foreach (var p in new[] { TodoPriority.High, TodoPriority.Medium, TodoPriority.Low, TodoPriority.None })
                {
                    var rows = open.Where(t => t.Priority == p)
                        .OrderBy(t => t.SortOrder)
                        .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                        .ThenByDescending(t => t.CreatedAt);
                    AddGroup(groups, PriorityHeader(p), rows);
                }
            }
            else
            {
                var today = DateTime.Today;
                AddGroup(groups, "Overdue", Sorted(open.Where(t => t.IsOverdue)));
                AddGroup(groups, "Today", Sorted(open.Where(t => t.DueDate is { } d && d.Date == today)));
                AddGroup(groups, "This week", Sorted(open.Where(t => t.DueDate is { } d && d.Date > today && d.Date <= today.AddDays(7))));
                AddGroup(groups, "Later", Sorted(open.Where(t => t.DueDate is { } d && d.Date > today.AddDays(7))));
                AddGroup(groups, "No date", Sorted(open.Where(t => t.DueDate is null)));
            }

            return new ObservableCollection<TodoTaskGroup>(groups);
        }

        private void BuildBoard()
        {
            var open = Filtered(_all.Where(t => !t.IsDone)).ToList();

            TodoTaskGroup Col(TodoPriority p) => new(PriorityHeader(p),
                open.Where(t => t.Priority == p)
                    .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
                    .ThenByDescending(t => t.CreatedAt));

            BoardHigh = Col(TodoPriority.High);
            BoardMedium = Col(TodoPriority.Medium);
            BoardLow = Col(TodoPriority.Low);
            BoardNone = Col(TodoPriority.None);
        }

        private ObservableCollection<TodoTaskGroup> BuildFinished()
        {
            var done = Filtered(_all.Where(t => t.IsDone)).ToList();
            var groups = new List<TodoTaskGroup>();
            var today = DateTime.Today;
            var weekStart = today.AddDays(-(int)today.DayOfWeek);

            IEnumerable<TodoTask> InWeek() => done.Where(t => (t.CompletedAt ?? t.UpdatedAt).Date >= weekStart);
            IEnumerable<TodoTask> LastWeek() => done.Where(t =>
            {
                var d = (t.CompletedAt ?? t.UpdatedAt).Date;
                return d >= weekStart.AddDays(-7) && d < weekStart;
            });
            IEnumerable<TodoTask> Earlier() => done.Where(t => (t.CompletedAt ?? t.UpdatedAt).Date < weekStart.AddDays(-7));

            AddGroup(groups, "This week", InWeek().OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt));
            AddGroup(groups, "Last week", LastWeek().OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt));
            AddGroup(groups, "Earlier", Earlier().OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt));
            return new ObservableCollection<TodoTaskGroup>(groups);
        }

        private static IOrderedEnumerable<TodoTask> Sorted(IEnumerable<TodoTask> rows) =>
            rows.OrderBy(t => t.PriorityRank)
                .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                .ThenBy(t => t.SortOrder)
                .ThenByDescending(t => t.CreatedAt);

        private static void AddGroup(List<TodoTaskGroup> groups, string header, IEnumerable<TodoTask> rows)
        {
            var list = rows.ToList();
            if (list.Count > 0) groups.Add(new TodoTaskGroup(header, list));
        }

        private static string PriorityHeader(TodoPriority p) => p switch
        {
            TodoPriority.High => "High priority",
            TodoPriority.Medium => "Medium priority",
            TodoPriority.Low => "Low priority",
            _ => "No priority"
        };

        // ---- commands ----
        [RelayCommand]
        public async Task QuickAddAsync()
        {
            var title = NewTaskTitle?.Trim();
            if (string.IsNullOrEmpty(title)) return;

            var task = new TodoTask
            {
                Title = title,
                Notes = string.IsNullOrWhiteSpace(NewTaskNotes) ? null : NewTaskNotes.Trim(),
                Priority = NewTaskPriority,
                DueDate = NewTaskDueDate?.Date,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SortOrder = _all.Count
            };
            HookTask(task);
            ResetComposer();

            try
            {
                await _repo.UpsertAsync(task);
                _all.Insert(0, task);
                Rebuild();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void NewTask() => ComposerOpen = true;

        [RelayCommand]
        public void CancelComposer() => ResetComposer();

        [RelayCommand]
        public void SetNewPriority(string priority)
        {
            if (Enum.TryParse<TodoPriority>(priority, out var p))
                NewTaskPriority = NewTaskPriority == p ? TodoPriority.None : p;
        }

        [RelayCommand]
        public void SetNewDue(string which)
        {
            NewTaskDueDate = which switch
            {
                "Today" => DateTime.Today,
                "Tomorrow" => DateTime.Today.AddDays(1),
                "Week" => DateTime.Today.AddDays(7),
                _ => null
            };
        }

        private void ResetComposer()
        {
            NewTaskTitle = string.Empty;
            NewTaskNotes = string.Empty;
            NewTaskPriority = TodoPriority.None;
            NewTaskDueDate = null;
            ComposerOpen = false;
        }

        [RelayCommand]
        public async Task ToggleDoneAsync(TodoTask? task)
        {
            if (task is null) return;
            var done = !task.IsDone;
            task.IsDone = done;
            task.CompletedAt = done ? DateTime.UtcNow : null;
            try
            {
                await _repo.SetDoneAsync(task.Id, done, task.CompletedAt);

                // Recurring task: completing it spawns the next occurrence.
                if (done && task.HasRecurrence) await SpawnNextOccurrenceAsync(task);

                if (SelectedTask == task && done) SelectedTask = null;
                Rebuild();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        private async Task SpawnNextOccurrenceAsync(TodoTask done)
        {
            var baseDate = done.DueDate ?? DateTime.Today;
            DateTime? next = done.RecurrenceRule switch
            {
                "Daily" => baseDate.AddDays(1),
                "Weekly" => baseDate.AddDays(7),
                "Monthly" => baseDate.AddMonths(1),
                _ => null
            };
            if (next is null) return;

            var copy = new TodoTask
            {
                Title = done.Title,
                Notes = done.Notes,
                Priority = done.Priority,
                DueDate = next.Value.Date,
                RecurrenceRule = done.RecurrenceRule,
                LinkedEntityType = done.LinkedEntityType,
                LinkedEntityId = done.LinkedEntityId,
                LinkedEntityLabel = done.LinkedEntityLabel,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SortOrder = done.SortOrder,
            };
            HookTask(copy);
            await _repo.UpsertAsync(copy);
            _all.Insert(0, copy);
        }

        [RelayCommand]
        public void Select(TodoTask? task)
        {
            if (SelectedTask != null) SelectedTask.IsSelected = false;
            SelectedTask = task;
            if (task != null) task.IsSelected = true;
        }

        [RelayCommand]
        public async Task MoveSelectedAsync(string direction)
        {
            if (SelectedTask is null) return;

            // Reorder within the group the task is currently shown in.
            var group = Groups.FirstOrDefault(g => g.Contains(SelectedTask));
            if (group is null) return;
            var i = group.IndexOf(SelectedTask);
            var j = direction == "Up" ? i - 1 : i + 1;
            if (j < 0 || j >= group.Count) return;

            var a = group[i];
            var b = group[j];
            (a.SortOrder, b.SortOrder) = (b.SortOrder, a.SortOrder);

            try
            {
                await _repo.ReorderAsync(new (Guid, int)[] { (a.Id, a.SortOrder), (b.Id, b.SortOrder) });
                Rebuild();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void Unlink()
        {
            if (SelectedTask is null) return;
            SelectedTask.LinkedEntityType = null;
            SelectedTask.LinkedEntityId = null;
            SelectedTask.LinkedEntityLabel = null;
            OnPropertyChanged(nameof(SelectedLinkTarget));
        }

        [RelayCommand]
        public async Task OpenLinkAsync(TodoTask? task)
        {
            task ??= SelectedTask;
            if (task?.LinkedEntityLabel is not { Length: > 0 } chip) return;

            // The board's search matches PR / RFQ / PO numbers, so navigating there with the chip
            // as the search term surfaces the linked record.
            if (Shell.Current != null)
            {
                await Shell.Current.GoToAsync("//prboard");
                if (PrListPageModel.Current is { } board) board.SearchText = chip;
            }
        }

        [RelayCommand]
        public async Task DeleteAsync(TodoTask? task)
        {
            task ??= SelectedTask;
            if (task is null) return;

            if (!string.IsNullOrWhiteSpace(task.Title) && Shell.Current != null)
            {
                var ok = await Shell.Current.DisplayAlertAsync("Delete task",
                    $"Delete “{task.Title}”?", "Delete", "Cancel");
                if (!ok) return;
            }

            try
            {
                await _repo.DeleteAsync(task.Id);
                UnhookTask(task);
                _all.Remove(task);
                if (SelectedTask == task) SelectedTask = null;
                Rebuild();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task ClearFinishedAsync()
        {
            if (Shell.Current != null)
            {
                var ok = await Shell.Current.DisplayAlertAsync("Clear finished",
                    "Permanently delete every completed task?", "Delete", "Cancel");
                if (!ok) return;
            }

            try
            {
                await _repo.DeleteCompletedAsync();
                foreach (var t in _all.Where(t => t.IsDone).ToList())
                {
                    UnhookTask(t);
                    _all.Remove(t);
                }
                Rebuild();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void ClearDueDate()
        {
            if (SelectedTask is null) return;
            SelectedTask.DueDate = null;
            OnPropertyChanged(nameof(SelectedHasDueDate));
        }

        [RelayCommand]
        public void SetDueToday()
        {
            if (SelectedTask is null) return;
            SelectedTask.DueDate = DateTime.Today;
            OnPropertyChanged(nameof(SelectedHasDueDate));
            OnPropertyChanged(nameof(SelectedDueDate));
        }

        [RelayCommand]
        public void SetView(string view) => CurrentView = view;

        [RelayCommand]
        public void ToggleGroupMode() => GroupMode = GroupMode == "Date" ? "Priority" : "Date";

        [RelayCommand]
        public void SetSelectedPriority(string priority)
        {
            if (SelectedTask is null) return;
            if (Enum.TryParse<TodoPriority>(priority, out var p)) SelectedTask.Priority = p;
        }

        // ---- autosave on the selected task's edits ----
        // Per-task generation: a debounced save for one task is never cancelled by an edit to a
        // different task (which would drop the first task's change until its next edit).
        private readonly Dictionary<Guid, int> _saveGeneration = new();

        private void HookTask(TodoTask task)
        {
            task.PropertyChanged -= OnTaskPropertyChanged;
            task.PropertyChanged += OnTaskPropertyChanged;
        }

        private void UnhookTask(TodoTask task) => task.PropertyChanged -= OnTaskPropertyChanged;

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not TodoTask task) return;

            switch (e.PropertyName)
            {
                case nameof(TodoTask.Priority):
                case nameof(TodoTask.DueDate):
                    Rebuild();
                    ScheduleSave(task);
                    break;
                case nameof(TodoTask.Title):
                case nameof(TodoTask.Notes):
                case nameof(TodoTask.RecurrenceRule):
                case nameof(TodoTask.LinkedEntityId):
                    ScheduleSave(task);
                    break;
            }
        }

        private void ScheduleSave(TodoTask task)
        {
            var generation = _saveGeneration[task.Id] = _saveGeneration.GetValueOrDefault(task.Id) + 1;
            Dispatcher.GetForCurrentThread()?.DispatchDelayed(TimeSpan.FromMilliseconds(400), async () =>
            {
                if (_saveGeneration.GetValueOrDefault(task.Id) != generation) return;
                if (string.IsNullOrWhiteSpace(task.Title)) return; // don't persist a blank new task yet
                try { await _repo.UpsertAsync(task); }
                catch (Exception ex) { _errorHandler.HandleError(ex); }
            });
        }
    }
}
