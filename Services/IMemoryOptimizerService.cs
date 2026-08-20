using System;
using System.Threading.Tasks;

namespace Procure.Services
{
    public interface IMemoryOptimizerService
    {
        void RecordActivity();
        void TrimMemory();
        Task TrimMemoryAsync();
    }
}
