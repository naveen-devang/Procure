using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Procure.Abstractions;
using Procure.Data.Repositories;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.Services
{
    /// <summary>
    /// Task reminders while Procure is open. Holds one timer, set for the next reminder only (and
    /// never longer than 15 minutes, so a sleeping PC or a clock change is caught up quickly); any
    /// task write re-reads the open tasks and resets it. What is due is shown as cards by the main
    /// window (MainWindow.Reminders.cs), which listens to <see cref="Changed"/>.
    ///
    /// Reminders that went off while Procure was closed are not shown one by one: the first read
    /// after launch gathers them into a single "came due while Procure was closed" card.
    /// </summary>
    public sealed class TaskReminderService
    {
        private static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(15);

        private readonly ITodoRepository _repo;
        private readonly ISettingsService _settings;
        private readonly IUiDispatcher _dispatcher;
        private readonly IErrorHandler _errorHandler;
        private readonly INavigationService _navigation;
        private readonly TodoPageModel _tasks;

        private DateTime _startedAt;
        private bool _started;
        private bool _firstRead = true;
        private int _generation;
        private readonly HashSet<Guid> _missed = new();

        public TaskReminderService(ITodoRepository repo, ISettingsService settings, IUiDispatcher dispatcher,
            IErrorHandler errorHandler, INavigationService navigation, TodoPageModel tasks)
        {
            _repo = repo;
            _settings = settings;
            _dispatcher = dispatcher;
            _errorHandler = errorHandler;
            _navigation = navigation;
            _tasks = tasks;
        }

        /// <summary>Reminders going off now, oldest first. Excludes the missed ones.</summary>
        public IReadOnlyList<ReminderRow> Due { get; private set; } = Array.Empty<ReminderRow>();

        /// <summary>How many went off while Procure was closed and are not dealt with yet.</summary>
        public int MissedCount => _missed.Count;

        public event Action? Changed;

        public TimeSpan DefaultTime => TaskReminders.ParseTime(_settings.DefaultReminderTime) ?? TaskReminders.DefaultTime;

        /// <summary>Called once the window is up.</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            _startedAt = DateTime.Now;
            TodoChangeNotifier.Written += () => _dispatcher.Post(Refresh);
            _settings.SettingsChanged += (_, e) =>
            {
                if (e.Key == nameof(ISettingsService.DefaultReminderTime)) _dispatcher.Post(Refresh);
            };
            Refresh();
        }

        public async void Refresh()
        {
            var generation = ++_generation;
            try
            {
                var rows = await _repo.GetRemindersAsync();
                if (generation != _generation) return;   // a newer read is on its way

                var now = DateTime.Now;
                var defaultTime = DefaultTime;
                var due = rows.Where(r => TaskReminders.IsDue(r, now, defaultTime))
                              .OrderBy(r => TaskReminders.RemindAt(r, defaultTime))
                              .ToList();

                if (_firstRead)
                {
                    _firstRead = false;
                    foreach (var r in due.Where(r => TaskReminders.RemindAt(r, defaultTime) < _startedAt)) _missed.Add(r.Id);
                }
                _missed.IntersectWith(due.Select(r => r.Id));   // done, deleted, moved or snoozed since
                Due = due.Where(r => !_missed.Contains(r.Id)).ToList();
                Changed?.Invoke();

                // The next reminder still to come, or a look again in 15 minutes, whichever is sooner.
                var next = rows.Select(r => TaskReminders.RemindAt(r, defaultTime))
                               .Where(at => at > now)
                               .DefaultIfEmpty(now + LongestWait)
                               .Min()!.Value;
                var wait = next - now;
                if (wait > LongestWait) wait = LongestWait;
                _dispatcher.PostDelayed(wait + TimeSpan.FromSeconds(1), () =>
                {
                    if (generation == _generation) Refresh();
                });
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ---- what the card buttons do -----------------------------------------------------

        private DateTime? FiredAt(ReminderRow row) => TaskReminders.RemindAt(row, DefaultTime);

        public async Task OpenAsync(ReminderRow row)
        {
            await DismissAsync(row);
            await _navigation.GoToAsync(AppRoute.Tasks);
            await _tasks.SelectByIdAsync(row.Id);
        }

        public async Task DoneAsync(ReminderRow row)
        {
            try { await _tasks.CompleteByIdAsync(row.Id); }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        public async Task SnoozeAsync(ReminderRow row, TimeSpan? delay)
        {
            var until = delay is { } d ? DateTime.Now + d : TaskReminders.TomorrowAt(DateTime.Now, DefaultTime);
            try { await _repo.SnoozeReminderAsync(row.Id, until); }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        public async Task DismissAsync(ReminderRow row)
        {
            if (FiredAt(row) is not { } at) return;
            try { await _repo.AcknowledgeReminderAsync(row.Id, at); }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        /// <summary>The "while Procure was closed" card: open the Tasks page, or let them all go.</summary>
        public async Task OpenMissedAsync()
        {
            await DismissMissedAsync();
            await _navigation.GoToAsync(AppRoute.Tasks);
        }

        public async Task DismissMissedAsync()
        {
            var ids = _missed.ToList();
            _missed.Clear();
            Changed?.Invoke();
            try
            {
                var rows = await _repo.GetRemindersAsync();
                foreach (var row in rows.Where(r => ids.Contains(r.Id)))
                    if (FiredAt(row) is { } at) await _repo.AcknowledgeReminderAsync(row.Id, at);
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }
    }
}
