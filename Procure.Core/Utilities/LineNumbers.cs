using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace Procure.Utilities
{
    /// <summary>
    /// Keeps the Sl No. shown beside each line of an editing list equal to the number that line gets
    /// on the PCR sheet or in Copy for email: the lines that count are numbered 1, 2, 3... top to
    /// bottom, the rest show a dash. Renumbers on every add, remove, move, paste or clear, and when a
    /// watched property (the tick, the name) changes - so combined, reordered and pasted lists stay
    /// right without each caller remembering to renumber.
    /// </summary>
    public sealed class LineNumbers<T> where T : class, INotifyPropertyChanged
    {
        public const string Dash = "–";

        private readonly Func<T, bool> _counts;
        private readonly Action<T, string> _set;
        private readonly string[] _watch;
        private readonly HashSet<T> _hooked = new(ReferenceEqualityComparer.Instance);
        private ObservableCollection<T>? _list;

        /// <param name="counts">Whether a line takes a number (false = dash).</param>
        /// <param name="set">Writes the number onto the line.</param>
        /// <param name="watch">Line properties whose change can alter <paramref name="counts"/>.</param>
        public LineNumbers(Func<T, bool> counts, Action<T, string> set, params string[] watch)
        {
            _counts = counts;
            _set = set;
            _watch = watch;
        }

        /// <summary>Follows <paramref name="list"/> from now on (and lets go of the previous one).</summary>
        public void Attach(ObservableCollection<T>? list)
        {
            if (ReferenceEquals(list, _list)) { Renumber(); return; }
            if (_list != null) _list.CollectionChanged -= OnListChanged;
            _list = list;
            if (_list != null) _list.CollectionChanged += OnListChanged;
            SyncHooks();
            Renumber();
        }

        private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            SyncHooks();
            Renumber();
        }

        private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null || Array.IndexOf(_watch, e.PropertyName) >= 0) Renumber();
        }

        // Re-derived from the list itself, so a Reset (Clear) that names no old items still unhooks them.
        private void SyncHooks()
        {
            var now = new HashSet<T>(_list ?? Enumerable.Empty<T>(), ReferenceEqualityComparer.Instance);
            foreach (var gone in _hooked.Where(h => !now.Contains(h)).ToList())
            {
                gone.PropertyChanged -= OnLineChanged;
                _hooked.Remove(gone);
            }
            foreach (var line in now)
                if (_hooked.Add(line)) line.PropertyChanged += OnLineChanged;
        }

        public void Renumber()
        {
            if (_list == null) return;
            var n = 0;
            foreach (var line in _list)
                _set(line, _counts(line) ? (++n).ToString() : Dash);
        }
    }

    internal static class LineNumbersSelfCheck
    {
        public static void Run()
        {
            var list = new ObservableCollection<Line> { new("A", true), new("B", false), new("", true), new("C", true) };
            var numbers = new LineNumbers<Line>(l => l.On && !string.IsNullOrWhiteSpace(l.Name), (l, no) => l.No = no, nameof(Line.On), nameof(Line.Name));
            numbers.Attach(list);
            Expect(list, "1 – – 2", "unticked and blank lines take a dash; the rest count in order");

            list[1].On = true;
            Expect(list, "1 2 – 3", "ticking a line numbers it and moves the ones below up");

            list[2].Name = "typed";
            Expect(list, "1 2 3 4", "naming a blank line numbers it");

            list.Move(3, 0);
            Expect(list, "1 2 3 4", "a reordered list renumbers top to bottom");

            var removed = list[0];
            list.RemoveAt(0);
            removed.On = false;
            Expect(list, "1 2 3", "a removed line closes the gap and is no longer followed");

            list.Clear();
            list.Add(new Line("P1 line", true));
            list.Add(new Line("P2 line", true));
            Expect(list, "1 2", "a cleared and refilled list (combined PRs, a paste) starts again at 1");

            var other = new ObservableCollection<Line> { new("X", true) };
            numbers.Attach(other);
            list[0].On = false;
            if (list[0].No != "1") throw new InvalidOperationException("LINE NUMBERS: the previous list is let go when another is attached");
            Expect(other, "1", "the newly attached list is numbered");
        }

        private static void Expect(IEnumerable<Line> list, string expected, string message)
        {
            var got = string.Join(" ", list.Select(l => l.No));
            if (got != expected) throw new InvalidOperationException($"LINE NUMBERS: {message}; expected \"{expected}\", got \"{got}\"");
        }

        private sealed class Line : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;
            private bool _on;
            private string _name;
            public string No { get; set; } = string.Empty;
            public Line(string name, bool on) { _name = name; _on = on; }
            public bool On { get => _on; set { _on = value; PropertyChanged?.Invoke(this, new(nameof(On))); } }
            public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        }
    }
}
