using System.Threading.Tasks;
using Procure.Models;

namespace Procure.Services
{
    public interface IDashboardMetricsService
    {
        Task<DashboardMetrics> GetMetricsAsync();
    }
}
