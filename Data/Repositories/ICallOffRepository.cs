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
        Task<List<MaterialGroupSummary>> GetMaterialSummariesAsync(string? searchTerm = null);

        /// <summary>One expanded material's lines, under the same search filter the summaries used.</summary>
        Task<List<CallOffLine>> GetLinesForMaterialAsync(string materialName, string? searchTerm = null);
        Task<List<PoItemCallOff>> GetHistoryAsync(Guid poItemId);
        Task LogCallOffAsync(PoItemCallOff entry);
        Task DeleteCallOffAsync(Guid id);
    }
}
