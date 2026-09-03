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

        // PR/RFQ/PO link targets, loaded once alongside the tasks and kept in memory (the typeahead
        // filters this list - no query per keystroke). _linkChip maps a target id to its short chip
        // label so a renamed target still shows correctly after a reload.
        private List<TaskLinkTarget> _linkTargets = new();
        private readonly Dictionary<Guid, string> _linkChip = new();

        public ObservableCollection<TaskLinkTarget> LinkResults { get; } = new();

        public string[] RecurrenceOptions { get; } = { "None", "Daily", "Weekly", "Monthly" };

        public bool IsVisible { get; set; }

        // "List" | "Board" | "Finished" - string rather than an enum so the XAML view toggle can
        // compare it with StringEqualsConverter directly.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsListView))]
        [NotifyPropertyChangedFor(nameof(IsBoardView))]
        [NotifyPropertyChangedFor(nameof(IsFinishedView))]
        [NotifyPropertyChangedFor(nameof(IsCalendarView))]
        [NotifyPropertyChangedFor(nameof(ShowListOrFinished))]
        [NotifyPropertyChangedFor(nameof(ShowQuickAdd))]
        public partial string CurrentView { get; set; } = "List";

        public bool ShowListOrFinished => CurrentView is "List" or "Finished";

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
        [NotifyPropertyChangedFor(nameof(SelectedRecurrence))]
        public partial TodoTask? SelectedTask { get; set; }

        // ---- link typeahead (detail panel) ----
        [ObservableProperty]
        public partial string LinkQuery { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool ShowLinkResults { get; set; }

        // ---- sub-tasks (detail panel) ----
        public ObservableCollection<TodoTask> SelectedSubtasks { get; } = new();

        [ObservableProperty]
        public partial string NewSubtaskTitle { get; set; } = string.Empty;

        public string SelectedSubtaskSummary =>
            SelectedSubtasks.Count == 0 ? "Subtasks"
            : $"Subtasks · {SelectedSubtasks.Count(s => s.IsDone)}/{SelectedSubtasks.Count}";

        // ---- calendar view: a 7-day week of columns ----
        public ObservableCollection<WeekDayColumn> WeekColumns { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WeekRangeLabel))]
        public partial DateTime WeekStart { get; set; } = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek);

        public string WeekRangeLabel => $"{WeekStart:d MMM} – {WeekStart.AddDays(6):d MMM}";

        // MAUI BindableLayout won't honour Grid.Column bound on a template root, so the page builds
        // the 7-column week grid in code-behind and listens for this.
        public event Action? WeekRebuilt;

        [ObservableProperty]
        public partial int OpenCount { get; set; }

        [ObservableProperty]
        public partial int TodayCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOverdue))]
        public partial int OverdueCount { get; set; }

        public bool HasOverdue => OverdueCount > 0;

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
        public bool IsCalendarView => CurrentView == "Calendar";
        public bool ShowQuickAdd => CurrentView is "List" or "Calendar";

        public Array PriorityOptions { get; } = Enum.GetValues(typeof(TodoPriority));

        public TodoPageModel(ITodoRepository repo, IErrorHandler errorHandler)
        {
            _repo = repo;
            _errorHandler = errorHandler;

            // DI singleton - lives for the process, so this is never unsubscribed. Fires when the
            // PR detail panel's task strip changes a linked task.
            Utilities.TodoChangeNotifier.Changed += OnExternalTodoChange;
        }

        private bool _selfRaised;

        private void RaiseChanged()
        {
            _selfRaised = true;
            Utilities.TodoChangeNotifier.NotifyChanged();
            _selfRaised = false;
        }

        private void OnExternalTodoChange()
        {
            if (_selfRaised) return;               // our own edit - already applied in memory
            if (IsVisible) _ = LoadAsync(force: true);
            else _loaded = false;                  // reload lazily on next visit
        }

        internal void UnsubscribeForTest() => Utilities.TodoChangeNotifier.Changed -= OnExternalTodoChange;

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
                _linkTargets = await _repo.GetLinkTargetsAsync();
                _linkChip.Clear();
                foreach (var t in _linkTargets) _linkChip[t.Id] = t.ChipLabel;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        private void ResolveLinkLabel(TodoTask t)
        {
            for (var i = 0; i < t.Links.Count; i++)
            {
                var link = t.Links[i];
                if (_linkChip.TryGetValue(link.EntityId, out var chip) && chip != link.Label)
                    // Replace (not mutate) so the collection raises CollectionChanged -> LinksBadge refreshes.
                    t.Links[i] = new TaskLink { EntityType = link.EntityType, EntityId = link.EntityId, Label = chip };
            }
        }

        // Top-level tasks only - sub-tasks live inside their parent's checklist, never in the
        // main list / board / calendar.
        private IEnumerable<TodoTask> TopLevel => _all.Where(t => t.ParentId is null);

        private void Rebuild()
        {
            OpenCount = TopLevel.Count(t => !t.IsDone);
            TodayCount = TopLevel.Count(t => !t.IsDone && t.DueDate is { } d && d.Date == DateTime.Today);
            OverdueCount = TopLevel.Count(t => t.IsOverdue);
            RefreshSubtaskBadges();

            switch (CurrentView)
            {
                case "Board":
                    BuildBoard();
                    Groups = new ObservableCollection<TodoTaskGroup>();
                    break;
                case "Calendar":
                    Groups = new ObservableCollection<TodoTaskGroup>();
                    BuildWeek(DueByDay());
                    break;
                case "Finished":
                    Groups = BuildFinished();
                    break;
                default:
                    Groups = BuildList();
                    break;
            }
        }

        private void RefreshSubtaskBadges()
        {
            var byParent = _all.Where(t => t.ParentId is { } p && p != Guid.Empty)
                               .GroupBy(t => t.ParentId!.Value)
                               .ToDictionary(g => g.Key, g => (done: g.Count(x => x.IsDone), total: g.Count()));
            foreach (var t in TopLevel)
                t.SubtaskBadge = byParent.TryGetValue(t.Id, out var c) ? $"{c.done}/{c.total}" : null;
        }

        // In-memory task->day index, computed once per Rebuild.
        private Dictionary<DateTime, List<TodoTask>> DueByDay() =>
            Filtered(TopLevel.Where(t => t.DueDate is not null))
                .GroupBy(t => t.DueDate!.Value.Date)
                .ToDictionary(g => g.Key,
                    g => g.OrderBy(t => t.IsDone).ThenBy(t => t.PriorityRank).ThenBy(t => t.Title).ToList());

        private void BuildWeek(Dictionary<DateTime, List<TodoTask>> byDay)
        {
            var cols = new List<WeekDayColumn>(7);
            for (var i = 0; i < 7; i++)
            {
                var d = WeekStart.Date.AddDays(i);
                cols.Add(new WeekDayColumn(d, i, byDay.GetValueOrDefault(d) ?? new List<TodoTask>()));
            }
            ReplaceAll(WeekColumns, cols);
            WeekRebuilt?.Invoke();
        }

        private static void ReplaceAll<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
        {
            target.Clear();
            foreach (var it in items) target.Add(it);
        }

        private ObservableCollection<TodoTaskGroup> BuildList()
        {
            var open = Filtered(TopLevel.Where(t => !t.IsDone)).ToList();
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
            var open = Filtered(TopLevel.Where(t => !t.IsDone)).ToList();

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
            var done = Filtered(TopLevel.Where(t => t.IsDone)).ToList();
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
                DueDate = NewTaskDueDate?.Date ?? DateTime.Today,   // no date picked -> due today
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
                if (task.HasLinks) RaiseChanged();
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
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SortOrder = done.SortOrder,
            };
            foreach (var l in done.Links)
                copy.Links.Add(new TaskLink { EntityType = l.EntityType, EntityId = l.EntityId, Label = l.Label });
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
            LinkQuery = string.Empty;
            ShowLinkResults = false;
            NewSubtaskTitle = string.Empty;
            RefreshSelectedSubtasks();
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

        // ---- link typeahead ----
        partial void OnLinkQueryChanged(string value)
        {
            var term = value?.Trim();
            LinkResults.Clear();
            if (string.IsNullOrEmpty(term) || term.Length < 2)
            {
                ShowLinkResults = false;
                return;
            }

            foreach (var t in _linkTargets
                         .Where(t => t.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
                         .Take(12))
                LinkResults.Add(t);

            ShowLinkResults = LinkResults.Count > 0;
        }

        [RelayCommand]
        public async Task PickLinkTargetAsync(TaskLinkTarget? target)
        {
            if (SelectedTask is null || target is null) return;
            var task = SelectedTask;
            LinkQuery = string.Empty;
            ShowLinkResults = false;

            if (task.Links.Any(l => l.EntityId == target.Id)) return;   // already linked
            task.Links.Add(new TaskLink { EntityType = target.Type, EntityId = target.Id, Label = target.ChipLabel });
            await PersistLinksAsync(task);
        }

        [RelayCommand]
        public async Task RemoveLinkAsync(TaskLink? link)
        {
            if (SelectedTask is null || link is null) return;
            var task = SelectedTask;
            var existing = task.Links.FirstOrDefault(l => l.EntityId == link.EntityId);
            if (existing is null) return;
            task.Links.Remove(existing);
            await PersistLinksAsync(task);
        }

        private async Task PersistLinksAsync(TodoTask task)
        {
            try { await _repo.SetLinksAsync(task.Id, task.Links.ToList()); RaiseChanged(); }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        // ---- calendar (week view) ----
        [RelayCommand] public void CalendarToday() { WeekStart = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek); BuildWeek(DueByDay()); }

        [RelayCommand] public void WeekPrev() { WeekStart = WeekStart.AddDays(-7); BuildWeek(DueByDay()); }
        [RelayCommand] public void WeekNext() { WeekStart = WeekStart.AddDays(7); BuildWeek(DueByDay()); }

        public async Task AddWeekColumnTaskAsync(WeekDayColumn? col)
        {
            var title = col?.NewTaskTitle?.Trim();
            if (col is null || string.IsNullOrEmpty(title)) return;

            var task = new TodoTask
            {
                Title = title,
                DueDate = col.Date.Date,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SortOrder = _all.Count,
            };
            HookTask(task);
            col.NewTaskTitle = string.Empty;
            try
            {
                await _repo.UpsertAsync(task);
                _all.Insert(0, task);
                col.Tasks.Add(task);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ---- sub-tasks ----
        private void RefreshSelectedSubtasks()
        {
            SelectedSubtasks.Clear();
            if (SelectedTask is null) return;
            foreach (var s in _all.Where(t => t.ParentId == SelectedTask.Id)
                                  .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt))
                SelectedSubtasks.Add(s);
            OnPropertyChanged(nameof(SelectedSubtaskSummary));
        }

        [RelayCommand]
        public async Task AddSubtaskAsync()
        {
            var title = NewSubtaskTitle?.Trim();
            if (string.IsNullOrEmpty(title) || SelectedTask is null) return;

            var sub = new TodoTask
            {
                ParentId = SelectedTask.Id,
                Title = title,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SortOrder = SelectedSubtasks.Count,
            };
            HookTask(sub);
            NewSubtaskTitle = string.Empty;

            try
            {
                await _repo.UpsertAsync(sub);
                _all.Add(sub);
                RefreshSelectedSubtasks();
                RefreshSubtaskBadges();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task ToggleSubtaskAsync(TodoTask? sub)
        {
            if (sub is null) return;
            var done = !sub.IsDone;
            sub.IsDone = done;
            sub.CompletedAt = done ? DateTime.UtcNow : null;
            try
            {
                await _repo.SetDoneAsync(sub.Id, done, sub.CompletedAt);
                OnPropertyChanged(nameof(SelectedSubtaskSummary));
                RefreshSubtaskBadges();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task DeleteSubtaskAsync(TodoTask? sub)
        {
            if (sub is null) return;
            try
            {
                await _repo.DeleteAsync(sub.Id);
                UnhookTask(sub);
                _all.Remove(sub);
                RefreshSelectedSubtasks();
                RefreshSubtaskBadges();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task OpenLinkAsync(TodoTask? task)
        {
            task ??= SelectedTask;
            if (task is null || task.Links.Count == 0) return;

            // The board's search splits on whitespace and ORs the terms, so every linked
            // PR / RFQ / PO number together surfaces all of them at once.
            var terms = string.Join(' ', task.Links.Select(l => l.Label).Where(l => l.Length > 0).Distinct());
            if (terms.Length == 0) return;

            if (Shell.Current != null)
            {
                await Shell.Current.GoToAsync("//prboard");
                if (PrListPageModel.Current is { } board) board.SearchText = terms;
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
                // FK ON DELETE CASCADE removed the sub-tasks in the DB; drop them from memory too.
                foreach (var child in _all.Where(t => t.ParentId == task.Id).ToList())
                {
                    UnhookTask(child);
                    _all.Remove(child);
                }
                if (SelectedTask == task) SelectedTask = null;
                Rebuild();
                if (task.HasLinks) RaiseChanged();
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
                try
                {
                    await _repo.UpsertAsync(task);
                    if (task.HasLinks || task.ParentId is not null) RaiseChanged();
                }
                catch (Exception ex) { _errorHandler.HandleError(ex); }
            });
        }
    }
}
