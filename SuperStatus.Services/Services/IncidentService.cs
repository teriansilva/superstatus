using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Extensions;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using System.Collections.Immutable;

namespace SuperStatus.Services.Services
{
    public interface IIncidentService
    {
        Task<IDictionary<DateTime, List<IncidentViewModel>>> GetIncidentViewModelSetForDays(int page = 1, int pageSize = 0);
    }

    public class IncidentService(IIncidentRepository incidentRepository) : IIncidentService
    {
        public async Task<IDictionary<DateTime, List<IncidentViewModel>>> GetIncidentViewModelSetForDays(int page = 1, int pageSize = 0)
        {
            IDictionary<DateTime, IList<Incident>> incidents = await incidentRepository.GetIncidentSetForDaysGroupedByDays(30, 100);
            return incidents.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Select(x => new IncidentViewModel(x)).ToList()
            );
        }

    }
}
