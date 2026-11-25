using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories;

/// <summary>
/// Issue #241/#253: persistence for the alert audit trail, mirroring
/// <see cref="IWebhookExecutionLogRepository"/>.
/// </summary>
public interface IAlertDeliveryLogRepository : IRepository<AlertDeliveryLog>
{
    /// <summary>Newest-first global log for the admin audit UI, with the owning
    /// check eager-loaded. <paramref name="failuresOnly"/> narrows to actual
    /// delivery failures (<see cref="AlertOutcome.Failed"/>; none in Phase A).</summary>
    Task<List<AlertDeliveryLog>> GetRecentWithCheckAsync(int count, bool failuresOnly, CancellationToken cancellationToken = default);

    /// <summary>Retention cleanup — single SQL DELETE, same approach as the webhook log.</summary>
    Task<int> BulkDeleteOlderThanXDaysAsync(int days, CancellationToken cancellationToken = default);
}

public class AlertDeliveryLogRepository : Repository<AlertDeliveryLog>, IAlertDeliveryLogRepository
{
    public AlertDeliveryLogRepository(SuperStatusDb context) : base(context) { }

    public async Task<List<AlertDeliveryLog>> GetRecentWithCheckAsync(int count, bool failuresOnly, CancellationToken cancellationToken = default)
    {
        int safeCount = Math.Clamp(count, 1, 500);
        IQueryable<AlertDeliveryLog> q = DbSet
            .Include(x => x.StatusCheck)
            .Include(x => x.AlertProfile)  // #291 Phase C: + the linked profile's name (null on pre-#291 rows)
            .AsNoTracking();
        if (failuresOnly)
        {
            q = q.Where(x => x.Outcome == AlertOutcome.Failed);
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
