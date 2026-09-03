using System;
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

        // Optional link to a PR / RFQ / PO. Label is denormalised for the row chip; it is
        // re-resolved on load so a renamed target still shows correctly.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLink))]
        public partial string? LinkedEntityType { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLink))]
        public partial Guid? LinkedEntityId { get; set; }

        [ObservableProperty]
        public partial string? LinkedEntityLabel { get; set; }

        public bool HasLink => LinkedEntityId is not null;
        public bool HasRecurrence => !string.IsNullOrEmpty(RecurrenceRule);

        // Selection highlight, mirrors CallOffLine.IsSelected - the row's fill opacity is
        // toggled off this, not off a Setter, to keep AppThemeBinding live-reactive.
        [ObservableProperty]
        public partial bool IsSelected { get; set; }

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
