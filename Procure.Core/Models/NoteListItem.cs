using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    // The sidebar row. Never carries Body - the whole point is that opening the Notes tab reads
    // only titles and snippets. Title/Snippet/Pinned/UpdatedAt are observable so an edit to the
    // open note updates its row in place without a list reload.
    public partial class NoteListItem : ObservableObject
    {
        public Guid Id { get; init; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayTitle))]
        public partial string Title { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSnippet))]
        public partial string? Snippet { get; set; }

        public bool HasSnippet => !string.IsNullOrEmpty(Snippet);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WhenLabel))]
        public partial DateTime UpdatedAt { get; set; }

        [ObservableProperty]
        public partial bool Pinned { get; set; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        // Mirrors Note.SortOrder; used only for the manual reorder in NotePageModel.
        public int SortOrder { get; set; }

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled note" : Title;

        public string WhenLabel
        {
            get
            {
                var span = DateTime.UtcNow - UpdatedAt;
                if (span < TimeSpan.FromMinutes(1)) return "just now";
                if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}m ago";
                if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}h ago";
                if (span < TimeSpan.FromDays(7)) return $"{(int)span.TotalDays}d ago";
                return UpdatedAt.ToLocalTime().ToString("d MMM");
            }
        }
    }
}
