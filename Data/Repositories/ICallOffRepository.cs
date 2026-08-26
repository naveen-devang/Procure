using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public interface ICallOffRepository
    {
        Task<List<CallOffLine>> GetAllCallOffLinesAsync();
        Task<List<PoItemCallOff>> GetHistoryAsync(Guid poItemId);
        Task LogCallOffAsync(PoItemCallOff entry);
        Task DeleteCallOffAsync(Guid id);
    }
}
