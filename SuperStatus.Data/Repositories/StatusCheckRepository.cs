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

        /// <summary>#320 Phase 2b: record an inbound heartbeat ping. A single atomic,
        /// indexed UPDATE that stamps <c>LastHeartbeatUtc = nowUtc</c> on the row whose
        /// token matches — no entity load, no change tracking. Returns true when a row
        /// was updated, false for an unknown/rotated token (the endpoint answers
        /// 204/404 on that and nothing else).</summary>
        Task<bool> RecordHeartbeatAsync(string token, DateTime nowUtc, CancellationToken cancellationToken = default);
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

        public async Task<bool> RecordHeartbeatAsync(string token, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(token)) return false;

            // Single set-based UPDATE through the partial-unique IX_StatusCheckSet_HeartbeatToken
            // index. No entity is materialised (so the token never enters the change tracker /
            // a log), and an unknown or rotated token simply matches zero rows → false → 404.
            // The ProviderType guard is defence-in-depth: a row's token is cleared when it's
            // converted away from heartbeat (StatusCheckService.ApplyProviderConfig), but even
            // a stale token must never record against a non-heartbeat row. "heartbeat" is the
            // stable HeartbeatCheckProvider.TypeId (kept as a literal — Data can't reference Services).
            int affected = await DbSet
                .Where(x => x.HeartbeatToken == token && x.ProviderType == "heartbeat")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastHeartbeatUtc, nowUtc), cancellationToken);

            return affected > 0;
        }

        #endregion

    }
}
