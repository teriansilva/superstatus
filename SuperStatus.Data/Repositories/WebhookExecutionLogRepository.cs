using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories;

public interface IWebhookExecutionLogRepository : IRepository<WebhookExecutionLog>
{
    /// <summary>
    /// Newest-first log entries for one status check. Plans against
    /// IX_WebhookExecutionLogSet_StatusCheckId_AttemptedUtc.
    /// </summary>
    Task<List<WebhookExecutionLog>> GetRecentForStatusCheckAsync(long statusCheckId, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Newest-first global log entries (admin view). Plans against
    /// IX_WebhookExecutionLogSet_AttemptedUtc.
    /// </summary>
    Task<List<WebhookExecutionLog>> GetRecentAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issue #107 Phase 2: newest-first global log for the admin audit UI,
    /// with the owning <see cref="WebhookExecutionLog.StatusCheck"/> eager-
    /// loaded (so the table can show the check title). When
    /// <paramref name="failuresOnly"/> is set, returns only actual wire
    /// failures (NonSuccess/Timeout/TransportFailure — never Success or the
    /// throttle Skipped rows), planning on the Outcome index.
    /// </summary>
    Task<List<WebhookExecutionLog>> GetRecentWithCheckAsync(int count, bool failuresOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retention cleanup. Single SQL DELETE; same approach as the
    /// HistoricalStatusData cleanup landed in #80.
    /// </summary>
    Task<int> BulkDeleteOlderThanXDaysAsync(int days, CancellationToken cancellationToken = default);
}

public class WebhookExecutionLogRepository : Repository<WebhookExecutionLog>, IWebhookExecutionLogRepository
{
    public WebhookExecutionLogRepository(SuperStatusDb context) : base(context) { }

    public async Task<List<WebhookExecutionLog>> GetRecentForStatusCheckAsync(long statusCheckId, int count, CancellationToken cancellationToken = default)
    {
        int safeCount = Math.Clamp(count, 1, 500);
        return await DbSet
            .Where(x => x.StatusCheckId == statusCheckId)
            .OrderByDescending(x => x.AttemptedUtc)
            .Take(safeCount)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<WebhookExecutionLog>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        int safeCount = Math.Clamp(count, 1, 500);
        return await DbSet
            .OrderByDescending(x => x.AttemptedUtc)
            .Take(safeCount)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<WebhookExecutionLog>> GetRecentWithCheckAsync(int count, bool failuresOnly, CancellationToken cancellationToken = default)
    {
        int safeCount = Math.Clamp(count, 1, 500);
        IQueryable<WebhookExecutionLog> q = DbSet
            .Include(x => x.StatusCheck)   // admin audit feed shows the owning check's title
            .Include(x => x.Webhook)       // #291 Phase B: + the target's name (null on pre-#291 rows)
            .AsNoTracking();
        if (failuresOnly)
        {
            // Actual wire failures only — excludes Success and the throttle
            // Skipped rows (which never hit the network). Plans on the Outcome
            // index (issue #107 data layer).
            q = q.Where(x => x.Outcome == WebhookOutcome.NonSuccess
                          || x.Outcome == WebhookOutcome.Timeout
                          || x.Outcome == WebhookOutcome.TransportFailure);
        }
        return await q
            .OrderByDescending(x => x.AttemptedUtc)
            .Take(safeCount)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> BulkDeleteOlderThanXDaysAsync(int days, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-days);
        return await DbSet.Where(x => x.AttemptedUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
