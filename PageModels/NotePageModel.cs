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
    // Owns the note list, the open note, and autosave. Formatting is not here - it is a code-behind
    // call into the NoteEditor control (same split as TodoPage keeps the week grid out of its model).
    public partial class NotePageModel : ObservableObject
    {
        private readonly INoteRepository _repo;
        private readonly IErrorHandler _errorHandler;

        private List<NoteListItem> _all = new();
        private readonly Dictionary<Guid, Note> _bodyCache = new();
        private bool _loaded;

        public ObservableCollection<NoteListItem> Notes { get; } = new();

        [ObservableProperty]
        public partial string FilterText { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        public partial Note? SelectedNote { get; set; }

        [ObservableProperty]
        public partial string SaveState { get; set; } = string.Empty;

        public bool HasSelection => SelectedNote is not null;
        public int NoteCount => _all.Count;

        // ---- PR / RFQ / PO link typeahead ----
        private List<TaskLinkTarget> _linkTargets = new();
        private readonly Dictionary<Guid, string> _linkChip = new();

        public ObservableCollection<TaskLinkTarget> LinkResults { get; } = new();

        [ObservableProperty]
        public partial string LinkQuery { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool ShowLinkResults { get; set; }

        // Raised when a note is opened; the page hands the RTF to the editor control.
        public event Action<string>? EditorLoadRequested;

        public NotePageModel(INoteRepository repo, IErrorHandler errorHandler)
        {
            _repo = repo;
            _errorHandler = errorHandler;
        }

        public Task PreloadDataAsync() => LoadListAsync();

        public async Task LoadListAsync(bool force = false)
        {
            if (_loaded && !force) return;
            try
            {
                _all = await _repo.GetListAsync();
                await LoadLinkTargetsAsync();
                _loaded = true;
                RebuildList();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ---- filter ----
        private int _filterGeneration;

        partial void OnFilterTextChanged(string value)
        {
            var generation = ++_filterGeneration;
            Dispatcher.GetForCurrentThread()?.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
            {
                if (generation == _filterGeneration) RebuildList();
            });
        }

        private void RebuildList()
        {
            var term = FilterText?.Trim();
            IEnumerable<NoteListItem> rows = _all;
            if (!string.IsNullOrEmpty(term))
                rows = _all.Where(n =>
                    n.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (n.Snippet?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));

            Notes.Clear();
            foreach (var n in rows) Notes.Add(n);
            OnPropertyChanged(nameof(NoteCount));
        }

        // ---- selection ----
        [RelayCommand]
        public async Task SelectAsync(NoteListItem? item)
        {
            if (item is null || SelectedNote?.Id == item.Id) return;
            await FlushPendingAsync();

            foreach (var n in _all) n.IsSelected = n.Id == item.Id;

            try
            {
                Note? note = _bodyCache.TryGetValue(item.Id, out var cached)
                    ? cached
                    : await _repo.GetAsync(item.Id);
                if (note is null) return;

                _bodyCache[note.Id] = note;
                ResolveLinkLabels(note);
                HookNote(note);
                SelectedNote = note;
                EditorLoadRequested?.Invoke(note.Body);
                SaveState = string.Empty;
                LinkQuery = string.Empty;
                ShowLinkResults = false;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // ---- links ----
        private async Task LoadLinkTargetsAsync()
        {
            try
            {
                _linkTargets = await _repo.GetLinkTargetsAsync();
                _linkChip.Clear();
                foreach (var t in _linkTargets) _linkChip[t.Id] = t.ChipLabel;
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        private void ResolveLinkLabels(Note note)
        {
            for (var i = 0; i < note.Links.Count; i++)
            {
                var link = note.Links[i];
                if (_linkChip.TryGetValue(link.EntityId, out var chip) && chip != link.Label)
                    note.Links[i] = new NoteLink { EntityType = link.EntityType, EntityId = link.EntityId, Label = chip };
            }
        }

        partial void OnLinkQueryChanged(string value)
        {
            var term = value?.Trim();
            LinkResults.Clear();
            if (string.IsNullOrEmpty(term) || term.Length < 2)
            {
                ShowLinkResults = false;
                return;
            }

            foreach (var t in _linkTargets.Where(t => t.Label.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(12))
                LinkResults.Add(t);
            ShowLinkResults = LinkResults.Count > 0;
        }

        [RelayCommand]
        public async Task PickLinkTargetAsync(TaskLinkTarget? target)
        {
            if (SelectedNote is null || target is null) return;
            var note = SelectedNote;
            LinkQuery = string.Empty;
            ShowLinkResults = false;
            if (note.Links.Any(l => l.EntityId == target.Id)) return;

            note.Links.Add(new NoteLink { EntityType = target.Type, EntityId = target.Id, Label = target.ChipLabel });
            try { await _repo.SetLinksAsync(note.Id, note.Links.ToList()); }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        [RelayCommand]
        public async Task RemoveLinkAsync(NoteLink? link)
        {
            if (SelectedNote is null || link is null) return;
            var note = SelectedNote;
            var existing = note.Links.FirstOrDefault(l => l.EntityId == link.EntityId);
            if (existing is null) return;

            note.Links.Remove(existing);
            try { await _repo.SetLinksAsync(note.Id, note.Links.ToList()); }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        [RelayCommand]
        public async Task OpenLinkAsync()
        {
            if (SelectedNote is null || SelectedNote.Links.Count == 0) return;
            var terms = string.Join(' ', SelectedNote.Links.Select(l => l.Label).Where(l => l.Length > 0).Distinct());
            if (terms.Length == 0 || Shell.Current is null) return;

            await Shell.Current.GoToAsync("//prboard");
            if (PrListPageModel.Current is { } board) board.SearchText = terms;
        }

        // ---- new / delete / pin / duplicate ----
        [RelayCommand]
        public async Task NewNoteAsync()
        {
            await FlushPendingAsync();

            var note = new Note
            {
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SortOrder = _all.Count == 0 ? 0 : _all.Min(n => n.SortOrder) - 1,
            };

            var row = new NoteListItem { Id = note.Id, Title = string.Empty, UpdatedAt = note.UpdatedAt, SortOrder = note.SortOrder };
            _all.Insert(0, row);
            _bodyCache[note.Id] = note;
            HookNote(note);

            try { await _repo.UpsertAsync(note, string.Empty); }
            catch (Exception ex) { _errorHandler.HandleError(ex); }

            RebuildList();
            foreach (var n in _all) n.IsSelected = n.Id == note.Id;
            SelectedNote = note;
            EditorLoadRequested?.Invoke(string.Empty);
        }

        [RelayCommand]
        public Task DeleteAsync(NoteListItem? item) => DeleteInternalAsync(item, confirm: true);

        // confirm: false is the self-check path - it must never raise a dialog.
        public async Task DeleteInternalAsync(NoteListItem? item, bool confirm)
        {
            item ??= _all.FirstOrDefault(n => n.Id == SelectedNote?.Id);
            if (item is null) return;

            if (confirm && Shell.Current is not null)
            {
                var ok = await Shell.Current.DisplayAlertAsync("Delete note",
                    $"Delete “{item.DisplayTitle}”?", "Delete", "Cancel");
                if (!ok) return;
            }

            try
            {
                await _repo.DeleteAsync(item.Id);
                _bodyCache.Remove(item.Id);
                _all.Remove(item);
                if (SelectedNote?.Id == item.Id)
                {
                    UnhookNote(SelectedNote);
                    SelectedNote = null;
                }
                RebuildList();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task TogglePinAsync(NoteListItem? item)
        {
            item ??= _all.FirstOrDefault(n => n.Id == SelectedNote?.Id);
            if (item is null) return;

            item.Pinned = !item.Pinned;
            if (SelectedNote?.Id == item.Id) SelectedNote.Pinned = item.Pinned;

            try { await _repo.SetPinnedAsync(item.Id, item.Pinned); }
            catch (Exception ex) { _errorHandler.HandleError(ex); }

            _all = _all.OrderByDescending(n => n.Pinned).ThenBy(n => n.SortOrder)
                       .ThenByDescending(n => n.UpdatedAt).ToList();
            RebuildList();
        }

        [RelayCommand]
        public async Task DuplicateAsync(NoteListItem? item)
        {
            item ??= _all.FirstOrDefault(n => n.Id == SelectedNote?.Id);
            if (item is null) return;

            try
            {
                var source = _bodyCache.TryGetValue(item.Id, out var c) ? c : await _repo.GetAsync(item.Id);
                if (source is null) return;

                var copy = new Note
                {
                    Title = string.IsNullOrWhiteSpace(source.Title) ? "Untitled note (copy)" : source.Title + " (copy)",
                    Body = source.Body,
                    Format = source.Format,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    SortOrder = source.SortOrder,
                };
                foreach (var l in source.Links)
                    copy.Links.Add(new NoteLink { EntityType = l.EntityType, EntityId = l.EntityId, Label = l.Label });
                await _repo.UpsertAsync(copy, source.Snippet ?? string.Empty);

                _bodyCache[copy.Id] = copy;
                HookNote(copy);
                var row = new NoteListItem
                {
                    Id = copy.Id, Title = copy.Title, Snippet = source.Snippet,
                    UpdatedAt = copy.UpdatedAt, SortOrder = copy.SortOrder,
                };
                _all.Insert(_all.IndexOf(item) + 1, row);
                RebuildList();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task MoveAsync(string direction)
        {
            if (SelectedNote is null) return;
            var i = Notes.ToList().FindIndex(n => n.Id == SelectedNote.Id);
            var j = direction == "Up" ? i - 1 : i + 1;
            if (i < 0 || j < 0 || j >= Notes.Count) return;

            var a = Notes[i];
            var b = Notes[j];
            (a.SortOrder, b.SortOrder) = (b.SortOrder, a.SortOrder);
            if (SelectedNote.Id == a.Id) SelectedNote.SortOrder = a.SortOrder;

            try
            {
                await _repo.ReorderAsync(new (Guid, int)[] { (a.Id, a.SortOrder), (b.Id, b.SortOrder) });
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }

            _all = _all.OrderByDescending(n => n.Pinned).ThenBy(n => n.SortOrder)
                       .ThenByDescending(n => n.UpdatedAt).ToList();
            RebuildList();
        }

        // ---- body edits from the editor ----
        public void OnBodyEdited(string rtf, string plainText)
        {
            if (SelectedNote is null) return;
            SelectedNote.Body = rtf;

            var row = _all.FirstOrDefault(n => n.Id == SelectedNote.Id);
            if (row is not null)
            {
                row.Snippet = Note.BuildSnippet(plainText);
                row.UpdatedAt = DateTime.UtcNow;
            }

            SaveState = "saving…";
            ScheduleSave(SelectedNote, plainText);
        }

        // ---- title edits ----
        private void HookNote(Note note)
        {
            note.PropertyChanged -= OnNotePropertyChanged;
            note.PropertyChanged += OnNotePropertyChanged;
        }

        private void UnhookNote(Note note) => note.PropertyChanged -= OnNotePropertyChanged;

        private void OnNotePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not Note note || e.PropertyName != nameof(Note.Title)) return;
            var row = _all.FirstOrDefault(n => n.Id == note.Id);
            if (row is not null) { row.Title = note.Title; row.UpdatedAt = DateTime.UtcNow; }
            SaveState = "saving…";
            ScheduleSave(note, note.Snippet ?? string.Empty);
        }

        // ---- debounced autosave, per note (copied from TodoPageModel.ScheduleSave) ----
        private readonly Dictionary<Guid, int> _saveGeneration = new();
        private readonly Dictionary<Guid, (Note note, string plain)> _pending = new();

        // Trailing debounce: a write happens only ~800 ms after the user stops typing. Every edit
        // supersedes the previous timer, so a burst of keystrokes is a single write.
        private void ScheduleSave(Note note, string plainText)
        {
            _pending[note.Id] = (note, plainText);
            var generation = _saveGeneration[note.Id] = _saveGeneration.GetValueOrDefault(note.Id) + 1;
            Dispatcher.GetForCurrentThread()?.DispatchDelayed(TimeSpan.FromMilliseconds(800), async () =>
            {
                if (_saveGeneration.GetValueOrDefault(note.Id) != generation) return;
                await SaveNoteAsync(note.Id);
            });
        }

        private async Task SaveNoteAsync(Guid id)
        {
            if (!_pending.Remove(id, out var p)) return;
            try
            {
                await _repo.UpsertAsync(p.note, p.plain);
                if (SelectedNote?.Id == id) SaveState = "saved";
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        // Flush any note with an unsaved edit - called on note switch and on page disappear.
        public async Task FlushPendingAsync()
        {
            foreach (var id in _pending.Keys.ToList())
                await SaveNoteAsync(id);
        }
    }
}
