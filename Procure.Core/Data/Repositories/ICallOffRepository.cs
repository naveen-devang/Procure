using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public interface ICallOffRepository
    {
        /// <summary>One row per material, aggregated in SQL, optionally filtered by a search term
        /// that matches material, vendor or PO number.</summary>
        /// <summary>One page of the material list, ordered by last activity in SQL - a slice sorted
        /// afterwards would be the wrong slice.</summary>
        Task<List<MaterialGroupSummary>> GetMaterialSummariesAsync(
            string? searchTerm = null, bool newestFirst = true, int skip = 0, int take = int.MaxValue);

        /// <summary>One expanded material's lines, under the same search filter the summaries used.</summary>
        Task<List<CallOffLine>> GetLinesForMaterialAsync(string materialName, string? searchTerm = null, int skip = 0, int take = int.MaxValue);
        Task<List<PoItemCallOff>> GetHistoryAsync(Guid poItemId);
        Task LogCallOffAsync(PoItemCallOff entry);
        Task DeleteCallOffAsync(Guid id);
    }
}
