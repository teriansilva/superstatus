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

        // Issue #106 PR2: incident create/update persistence. The base
        // Repository<Incident> already implements these; exposed on the
        // interface so IncidentService can save through the abstraction.
        Task<Incident> AddAndSave(Incident entity, CancellationToken cancellation = default);
        Task<Incident> UpdateAndSave(Incident entity, CancellationToken cancellation = default);

        /// <summary>
        /// All open incidents (Resolved=false) that the operator has marked
        /// VisibleToPublic. Newest first. Used by the public /api/status
        /// endpoint (issue #108) — must never leak operator-drafted or
        /// resolved incidents.
        /// </summary>
        Task<List<Incident>> GetOpenPublicIncidents();

        /// <summary>
        /// Incidents resolved within the last <paramref name="daysBack"/>
        /// days (Resolved=true and ResolvedUtc set inside the window). Used
        /// for MTTR (issue #106). Rows resolved before the entity carried a
        /// ResolvedUtc are excluded (null ResolvedUtc) — MTTR only counts
        /// incidents with a real measured duration.
        /// </summary>
        Task<List<Incident>> GetResolvedIncidentsInWindow(int daysBack);

        /// <summary>#168: the single OPEN auto-generated incident linked to a check
        /// (or null). The partial unique index guarantees there is at most one.</summary>
        Task<Incident?> GetOpenAutoIncidentForCheck(long statusCheckId);
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

        public async Task<List<Incident>> GetOpenPublicIncidents()
        {
            return await DbSet
                .Where(x => !x.Resolved && x.VisibleToPublic)
                .OrderByDescending(x => x.Created)
                .ToListAsync();
        }

        public async Task<List<Incident>> GetResolvedIncidentsInWindow(int daysBack)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-daysBack);
            return await DbSet
                .Where(x => x.Resolved && x.ResolvedUtc != null && x.ResolvedUtc >= cutoff)
                .ToListAsync();
        }

        public async Task<Incident?> GetOpenAutoIncidentForCheck(long statusCheckId)
        {
            return await DbSet.FirstOrDefaultAsync(x =>
                x.SourceStatusCheckId == statusCheckId && !x.Resolved && x.AuotmaticallyGeneratedReport);
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
