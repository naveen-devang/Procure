using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    // One column of the Week view. Rebuilt on week navigation (7 per week); Tasks are shared
    // instances from _all. NewTaskTitle is the column's own add field.
    public partial class WeekDayColumn : ObservableObject
    {
        public WeekDayColumn(DateTime date, int colIndex, IEnumerable<TodoTask> tasks)
        {
            Date = date;
            ColIndex = colIndex;
            Tasks = new ObservableCollection<TodoTask>(tasks);
        }

        public DateTime Date { get; }
        public int ColIndex { get; }
        public ObservableCollection<TodoTask> Tasks { get; }

        public string DayName => Date.ToString("ddd").ToUpperInvariant();
        public int Day => Date.Day;
        public bool IsToday => Date.Date == DateTime.Today;
        public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        [ObservableProperty]
        public partial string NewTaskTitle { get; set; } = string.Empty;
    }
}
