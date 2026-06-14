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

        /// <summary>#293: per-SLA linked check titles (ordered), for
        /// LinkedEntitySummary on the /admin/slas surface.</summary>
        Task<Dictionary<long, List<string>>> GetSlaLinkedCheckTitlesAsync(CancellationToken cancellationToken = default);
    }
    public class StatusCheckRepository : Repository<StatusCheck>, IStatusCheckRepository
    {

        public StatusCheckRepository(SuperStatusDb context) : base(context)
        {
        }

        #region GetData

        // #293: the linked SLA rides along on both load paths — it is the slow
        // threshold's source for classification and the VM's effective value.
        public async Task<IPagedResult<StatusCheck>> GetStatusCheckSet(int page = 1, int pageSize = 0)
        {
            return await DbSet.Include(x => x.Sla).OrderByDescending(x => x.Title).GetPagedAsync(page, pageSize);
        }

        public async Task<StatusCheck?> GetStatusCheckById(long statusCheckId)
        {
            return await DbSet.Include(x => x.Sla).FirstOrDefaultAsync(x => x.Id == statusCheckId);
        }

        public async Task<Dictionary<long, List<string>>> GetSlaLinkedCheckTitlesAsync(CancellationToken cancellationToken = default)
        {
            var rows = await DbSet
                .AsNoTracking()
                .Where(x => x.SlaId != null)
                .Select(x => new { SlaId = x.SlaId!.Value, x.Title })
                .ToListAsync(cancellationToken);
            return rows.GroupBy(x => x.SlaId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Title).OrderBy(t => t).ToList());
        }

        #endregion

    }
}
