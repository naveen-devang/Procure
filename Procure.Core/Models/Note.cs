using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    // A freeform note. Body is RTF produced by the RichEditBox (Format == "rtf"). Loaded in full
    // only when the note is opened - the list binds NoteListItem instead.
    public partial class Note : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // PR / RFQ / PO links. Labels are re-resolved on load (NotePageModel.ResolveLinkLabels).
        public ObservableCollection<NoteLink> Links { get; } = new();

        public Note()
        {
            Links.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasLinks));
                OnPropertyChanged(nameof(LinksBadge));
            };
        }

        public bool HasLinks => Links.Count > 0;

        public string LinksBadge => Links.Count switch
        {
            0 => string.Empty,
            1 => Links[0].Label,
            _ => $"{Links[0].Label}  +{Links.Count - 1}",
        };

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

        // First ~120 chars of the plain text, whitespace collapsed to single spaces so the list row
        // is always one line. Shared by NoteRepository (persisted) and NotePageModel (live edits).
        public static string? BuildSnippet(string? plainText)
        {
            if (string.IsNullOrWhiteSpace(plainText)) return null;
            var collapsed = System.Text.RegularExpressions.Regex.Replace(plainText.Trim(), @"\s+", " ");
            return collapsed.Length <= 120 ? collapsed : collapsed[..120];
        }
    }
}
