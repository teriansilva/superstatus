using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;
using System;
using System.Linq.Expressions;

namespace SuperStatus.Data.Repositories
{
    public interface ISuperStatusRepository : IBaseRepository
    {
        Task<HistoricalStatusData?> GetMostRecentHistoricalStatusData(long statusCheckId);
        Task<HistoricalStatusAction?> GetMostRecentHistoricalStatusAction(long statusCheckId);
        Task<IPagedResult<HistoricalStatusData>> GetHistoricalStatusDataSetForStatusCheckId(long statusCheckId, int page = 1, int pageSize = 0);
        Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataSetForDaysGroupedByDays(long statusCheckId, int timeRangeInDays, int maxBatchSize);
        Task<List<HistoricalStatusData>> GetHistoricalStatusDataFailuresOverviewSetForDaysGroupedByDays(StatusCheck statusCheck, int timeRangeInDays);
        Task CleanUpHistoricalStatusDataOlderThanXDays(int days);
        void Dispose();
    }
    public class SuperStatusRepository : BaseRepository, ISuperStatusRepository, IDisposable
    {
        private bool isDisposed;

        public SuperStatusRepository(IDbContextFactory<SuperStatusContext> dbContextFactory, ILogger<SuperStatusRepository> logger) : base(dbContextFactory, logger)
        {
        }

        #region GetData

        public async Task<HistoricalStatusData?> GetMostRecentHistoricalStatusData(long statusCheckId)
        {
                return await context.HistoricalStatusDataSet.Where(x => x.StatusCheckId == statusCheckId).OrderByDescending(x => x.TimeOfCheckUTC).FirstOrDefaultAsync();
        }

        public async Task<HistoricalStatusAction?> GetMostRecentHistoricalStatusAction(long statusCheckId)
        {
            return await context.HistoricalStatusActionSet.Where(x => x.StatusCheckId == statusCheckId).OrderByDescending(x => x.TimeOfExecutionUTC).FirstOrDefaultAsync();
        }

        public async Task<IPagedResult<HistoricalStatusData>> GetHistoricalStatusDataSetForStatusCheckId(long statusCheckId, int page = 1, int pageSize = 0)
        {
            return await context.HistoricalStatusDataSet.Where(x => x.StatusCheckId == statusCheckId).OrderByDescending(x => x.TimeOfCheckUTC).GetPagedAsync(page, pageSize);
        }

        public async Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataSetForDaysGroupedByDays(long statusCheckId, int timeRangeInDays, int maxBatchSize)
        {
            DateTime referenceTime = DateTime.UtcNow;
            DateTime startTime = referenceTime.AddDays(-timeRangeInDays);

            var historicalData = await context.HistoricalStatusDataSet
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

            return await context.HistoricalStatusDataSet
                .Where(x => x.StatusCheckId == statusCheck.Id 
                    && x.TimeOfCheckUTC >= startTime 
                    && x.TimeOfCheckUTC <= referenceTime
                    && (x.HttpStatusCode != statusCheck.ExpectedStatusCode 
                    || x.ResponseTimeInMs > statusCheck.ExpectedResponseTimeInMs
                    || x.CheckFailed))
                .ToListAsync();
        }

        #endregion

        #region CleanUp
        public async Task CleanUpHistoricalStatusDataOlderThanXDays(int days)
        {
            var result = context.HistoricalStatusDataSet.Where(x => x.TimeOfCheckUTC < DateTime.UtcNow.AddDays(-days));
            context.HistoricalStatusDataSet.RemoveRange(result);
            await context.SaveChangesAsync();
        }

        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed) return;

            if (disposing)
            {
                context.Dispose();
            }


            isDisposed = true;
        }

    }
}
