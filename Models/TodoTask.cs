using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public enum TodoPriority
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    // Personal checklist row. One TodoTask table; the whole list is loaded once and kept in
    // memory (see TodoPageModel) - a personal list is hundreds of rows at most. Fields past
    // SortOrder are reserved for later phases and stay null until then.
    public partial class TodoTask : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [ObservableProperty]
        public partial string Title { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string? Notes { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PriorityRank))]
        public partial TodoPriority Priority { get; set; }

        [ObservableProperty]
        public partial bool IsDone { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DueLabel))]
        [NotifyPropertyChangedFor(nameof(IsOverdue))]
        [NotifyPropertyChangedFor(nameof(HasDueDate))]
        public partial DateTime? DueDate { get; set; }

        [ObservableProperty]
        public partial DateTime? CompletedAt { get; set; }

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Guid? ParentId { get; set; }
        public DateTime? PlannedForDate { get; set; }

        // "Daily" | "Weekly" | "Monthly" | null. On completion a task with a rule spawns its next
        // occurrence (see TodoPageModel.ToggleDoneAsync).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRecurrence))]
        public partial string? RecurrenceRule { get; set; }

        // Links to PRs / RFQs / POs - a task can carry several. Labels are denormalised for the
        // chips and re-resolved on load (TodoPageModel.ResolveLinkLabel).
        public ObservableCollection<TaskLink> Links { get; } = new();

        public TodoTask()
        {
            Links.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasLinks));
                OnPropertyChanged(nameof(LinksBadge));
            };
        }

        public bool HasLinks => Links.Count > 0;

        // Row-chip text: the first link, plus "+N" when there are more.
        public string LinksBadge => Links.Count switch
        {
            0 => string.Empty,
            1 => Links[0].Label,
            _ => $"{Links[0].Label}  +{Links.Count - 1}",
        };

        public bool HasRecurrence => !string.IsNullOrEmpty(RecurrenceRule);

        // Selection highlight, mirrors CallOffLine.IsSelected - the row's fill opacity is
        // toggled off this, not off a Setter, to keep AppThemeBinding live-reactive.
        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        // "2/5" when this task has sub-tasks; null otherwise. Populated by TodoPageModel.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSubtasks))]
        public partial string? SubtaskBadge { get; set; }

        public bool HasSubtasks => !string.IsNullOrEmpty(SubtaskBadge);

        // High first when sorting within a group.
        public int PriorityRank => 3 - (int)Priority;

        public bool HasDueDate => DueDate.HasValue;

        public bool IsOverdue => DueDate is { } d && !IsDone && d.Date < DateTime.Today;

        public string? DueLabel
        {
            get
            {
                if (DueDate is not { } d) return null;
                var days = (d.Date - DateTime.Today).Days;
                return days switch
                {
                    0 => "Today",
                    1 => "Tomorrow",
                    -1 => "Yesterday",
                    < -1 => $"{-days} days ago",
                    >= 2 and <= 6 => d.ToString("ddd"),
                    _ => d.ToString("d MMM")
                };
            }
        }
    }
}
