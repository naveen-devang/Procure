using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public interface INoteRepository
    {
        // The sidebar list - Id, Title, Snippet, Pinned, UpdatedAt. Never the body.
        Task<List<NoteListItem>> GetListAsync();

        // One full note, body included. Called when a note is opened.
        Task<Note?> GetAsync(Guid id);

        // Full write. plainText is the RichEditBox's plain-text mirror, used to store Snippet
        // without parsing RTF here.
        Task UpsertAsync(Note note, string plainText);

        Task SetTitleAsync(Guid id, string title);
        Task SetPinnedAsync(Guid id, bool pinned);
        Task DeleteAsync(Guid id);
        Task ReorderAsync(IReadOnlyList<(Guid Id, int SortOrder)> rows);
    }
}
