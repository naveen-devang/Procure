using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public interface ICustomColumnRepository
    {
        Task<List<CustomColumnDefinition>> GetAllDefinitionsAsync();
        Task SaveDefinitionAsync(CustomColumnDefinition definition);
        Task DeleteDefinitionAsync(Guid id);

        Task<List<CustomFieldValue>> GetValuesForPrAsync(Guid prId);
        Task SaveValuesForPrAsync(Guid prId, IEnumerable<CustomFieldValue> values);
    }
}
