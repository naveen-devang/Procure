using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    // A freeform note. Body is RTF produced by the RichEditBox (Format == "rtf"). Loaded in full
    // only when the note is opened - the list binds NoteListItem instead.
    public partial class Note : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [ObservableProperty]
        public partial string Title { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Body { get; set; } = string.Empty;

        public string Format { get; set; } = "rtf";

        [ObservableProperty]
        public partial bool Pinned { get; set; }

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // First ~120 chars of the plain text, used for the list row. Recomputed on every save.
        public string? Snippet { get; set; }

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled note" : Title;
    }
}
