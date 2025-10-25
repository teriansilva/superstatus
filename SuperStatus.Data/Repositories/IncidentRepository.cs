using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public interface IIncidentRepository
    {
        Task<Incident?> GetIncidentById(long incidentId);
        Task<IPagedResult<Incident>> GetIncidentSet(int page = 1, int pageSize = 0);
        Task<IDictionary<DateTime, IList<Incident>>> GetIncidentSetForDaysGroupedByDays(int timeRangeInDays, int maxIncidentsPerDay);
    }

    public class IncidentRepository : Repository<Incident>, IIncidentRepository
    {
        public IncidentRepository(SuperStatusDb context) : base(context)
        {
        }

        #region GetData

        public async Task<IPagedResult<Incident>> GetIncidentSet(int page = 1, int pageSize = 0)
        {
            return await DbSet.OrderByDescending(x => x.Created).GetPagedAsync(page, pageSize);
        }

        public async Task<Incident?> GetIncidentById(long incidentId)
        {
            return await DbSet.FirstOrDefaultAsync(x => x.Id == incidentId);
        }

        public async Task<IDictionary<DateTime, IList<Incident>>> GetIncidentSetForDaysGroupedByDays(int timeRangeInDays, int maxIncidentsPerDay)
        {
            DateTime referenceTime = DateTime.UtcNow;
            DateTime startTime = referenceTime.AddDays(-timeRangeInDays);

            //create timeRangeInDays list of datetime
            List<DateTime> days = new List<DateTime>();
            for (int i = 0; i <= timeRangeInDays; i++)
            {
                days.Add(referenceTime.AddDays(-i).Date);
            }

            var result = await DbSet
                .Where(x => x.Created >= startTime
                    && x.Created <= referenceTime)
                .GroupBy(x => x.Created.Date)
                .ToListAsync();

            IDictionary<DateTime, IList<Incident>> incidentsByDay = new Dictionary<DateTime, IList<Incident>>();
            
            foreach (var day in days)
            {
                var incidentsForDay = result
                    .Where(g => g.Key.Date == day)
                    .SelectMany(g => g)
                    .OrderByDescending(x => x.Created)
                    .Take(maxIncidentsPerDay).ToList();
                incidentsByDay[day] = incidentsForDay;
            }

            return incidentsByDay;
        }

        #endregion

    }
}
