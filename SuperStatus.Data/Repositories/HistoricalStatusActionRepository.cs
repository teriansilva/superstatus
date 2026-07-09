using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public interface IHistoricalStatusActionRepository : IRepository<HistoricalStatusAction>
    {
        Task<HistoricalStatusAction?> GetMostRecentHistoricalStatusAction(long statusCheckId);
    }
    public class HistoricalStatusActionRepository : Repository<HistoricalStatusAction>, IHistoricalStatusActionRepository
    {
        public HistoricalStatusActionRepository(SuperStatusDb context) : base(context)
        {
        }

        public async Task<HistoricalStatusAction?> GetMostRecentHistoricalStatusAction(long statusCheckId)
        {
            return await DbSet.Where(x => x.StatusCheckId == statusCheckId).OrderByDescending(x => x.TimeOfExecutionUTC).FirstOrDefaultAsync();
        }



    }
}
