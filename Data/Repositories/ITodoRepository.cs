using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public interface ITodoRepository
    {
        // One full load, ordered. The whole personal list stays in memory after this.
        Task<List<TodoTask>> GetAllAsync();

        // INSERT .. ON CONFLICT(Id) DO UPDATE - covers both new tasks and edits.
        Task UpsertAsync(TodoTask task);

        // Targeted write for the row checkbox, so toggling done never rewrites a title/notes
        // the user might be editing in the panel at the same moment.
        Task SetDoneAsync(Guid id, bool done, DateTime? completedAt);

        Task DeleteAsync(Guid id);

        // "Clear finished".
        Task DeleteCompletedAsync();

        // Phase 2 manual reorder - persists SortOrder for a batch of rows.
        Task ReorderAsync(IReadOnlyList<(Guid Id, int SortOrder)> rows);

        // PRs / RFQs / POs a task can be linked to, newest first. Read straight from the shared
        // database - no dependency on the PR repository.
        Task<List<TaskLinkTarget>> GetLinkTargetsAsync();
    }
}
