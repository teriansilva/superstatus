using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public interface IHistoricalStatusDataRepository : IRepository<HistoricalStatusData>
    {
        Task<HistoricalStatusData?> GetMostRecentHistoricalStatusData(long statusCheckId);
        Task<IPagedResult<HistoricalStatusData>> GetHistoricalStatusDataSetForStatusCheckId(long statusCheckId, int page = 1, int pageSize = 0);
        Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataSetForDaysGroupedByDays(long statusCheckId, int timeRangeInDays);
        Task<List<HistoricalStatusData>> GetHistoricalStatusDataFailuresOverviewSetForDaysGroupedByDays(StatusCheck statusCheck, int timeRangeInDays);
        Task<List<HistoricalStatusData>> GetHistoricalStatusDataOlderThanXDays(int days);
    }
    public class HistoricalStatusDataRepository : Repository<HistoricalStatusData>, IHistoricalStatusDataRepository
    {
        public HistoricalStatusDataRepository(SuperStatusDb context) : base(context)
        {
        }

        public async Task<HistoricalStatusData?> GetMostRecentHistoricalStatusData(long statusCheckId)
        {
                return await DbSet.Where(x => x.StatusCheckId == statusCheckId).OrderByDescending(x => x.TimeOfCheckUTC).FirstOrDefaultAsync();
        }

        public async Task<IPagedResult<HistoricalStatusData>> GetHistoricalStatusDataSetForStatusCheckId(long statusCheckId, int page = 1, int pageSize = 0)
        {
            return await DbSet.Where(x => x.StatusCheckId == statusCheckId).OrderByDescending(x => x.TimeOfCheckUTC).GetPagedAsync(page, pageSize);
        }

        public async Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataSetForDaysGroupedByDays(long statusCheckId, int timeRangeInDays)
        {
            DateTime referenceTime = DateTime.UtcNow;
            DateTime startTime = referenceTime.AddDays(-timeRangeInDays);

            var historicalData = await DbSet
                .Where(x => x.StatusCheckId == statusCheckId && x.TimeOfCheckUTC >= startTime && x.TimeOfCheckUTC <= referenceTime)
                .OrderByDescending(x => x.TimeOfCheckUTC)
                .ToListAsync();

            return historicalData
                .GroupBy(x => x.TimeOfCheckUTC.Date)
                .ToDictionary(y => y.Key.Date, y => y.OrderByDescending(x => x.TimeOfCheckUTC).ToList());
        }

        public async Task<List<HistoricalStatusData>> GetHistoricalStatusDataFailuresOverviewSetForDaysGroupedByDays(StatusCheck statusCheck, int timeRangeInDays)
        {
            DateTime referenceTime = DateTime.UtcNow;
            DateTime startTime = referenceTime.AddDays(-timeRangeInDays);

            return await DbSet
                .Where(x => x.StatusCheckId == statusCheck.Id 
                    && x.TimeOfCheckUTC >= startTime 
                    && x.TimeOfCheckUTC <= referenceTime
                    && (x.HttpStatusCode != statusCheck.ExpectedStatusCode 
                    || x.ResponseTimeInMs > statusCheck.ExpectedResponseTimeInMs
                    || x.CheckFailed))
                .ToListAsync();
        }

        public async Task<List<HistoricalStatusData>> GetHistoricalStatusDataOlderThanXDays(int days)
        {
            return await DbSet.Where(x => x.TimeOfCheckUTC < DateTime.UtcNow.AddDays(-days)).ToListAsync();
        }

    }
}
