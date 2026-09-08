using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Procure.Models
{
    // A section in the grouped list ("Today", "High", "Week of 6 Oct"). Deriving from
    // ObservableCollection is what MAUI's CollectionView IsGrouped="True" expects - it
    // virtualises rows natively, so a long list costs only the visible rows plus the headers.
    // Rebuilt wholesale by TodoPageModel; never mutated in place.
    public class TodoTaskGroup : ObservableCollection<TodoTask>
    {
        public TodoTaskGroup(string header, IEnumerable<TodoTask> tasks) : base(new List<TodoTask>(tasks))
        {
            Header = header;
        }

        public string Header { get; }

        public string CountLabel => Count.ToString();
    }
}
