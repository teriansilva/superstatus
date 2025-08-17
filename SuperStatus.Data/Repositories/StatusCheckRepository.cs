using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public interface IStatusCheckRepository : IRepository<StatusCheck>
    {
        Task<IPagedResult<StatusCheck>> GetStatusCheckSet(int page = 1, int pageSize = 0);
        Task<StatusCheck?> GetStatusCheckById(long statusCheckId);
    }
    public class StatusCheckRepository : Repository<StatusCheck>, IStatusCheckRepository
    {

        public StatusCheckRepository(SuperStatusDb context) : base(context)
        {
        }

        #region GetData

        public async Task<IPagedResult<StatusCheck>> GetStatusCheckSet(int page = 1, int pageSize = 0)
        {
            return await DbSet.OrderByDescending(x => x.Title).GetPagedAsync(page, pageSize);
        }

        public async Task<StatusCheck?> GetStatusCheckById(long statusCheckId)
        {
            return await DbSet.FirstOrDefaultAsync(x => x.Id == statusCheckId);
        }

        #endregion

    }
}
