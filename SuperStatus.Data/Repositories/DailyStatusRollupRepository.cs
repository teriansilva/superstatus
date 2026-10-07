using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public interface IDailyStatusRollupRepository
    {
        /// <summary>Persisted daily tallies for one check on/after <paramref name="sinceDayUtc"/>
        /// (a UTC date). Tiny result (≤ window days); the dashboard reads this
        /// instead of aggregating raw ticks.</summary>
        Task<List<DailyStatusRollup>> GetSinceAsync(long statusCheckId, DateTime sinceDayUtc, CancellationToken cancellationToken = default);

        /// <summary>Issue #138 (PR-B): persisted daily tallies on/after
        /// <paramref name="sinceDayUtc"/> for MANY checks in one query — the
        /// batched counterpart of <see cref="GetSinceAsync"/> for the dashboard's
        /// set-based read path.</summary>
        Task<List<DailyStatusRollup>> GetSinceForChecksAsync(IReadOnlyCollection<long> statusCheckIds, DateTime sinceDayUtc, CancellationToken cancellationToken = default);

        /// <summary>Insert-or-update the tally for (check, day). Idempotent — the
        /// rollup job re-runs the recent days every tick and a re-run must not
        /// duplicate or drift.</summary>
        Task UpsertAsync(long statusCheckId, DateTime dayUtc, int total, int down, int degraded, int unreachable = 0, CancellationToken cancellationToken = default);

        /// <summary>True if any rollup rows exist — used to decide a one-time backfill.</summary>
        Task<bool> AnyAsync(CancellationToken cancellationToken = default);

        /// <summary>Issue #138 (PR-C1): the persisted rollup-computation version
        /// the stored rollups reflect; 0 when no marker row exists (fresh install
        /// or pre-marker upgrade). Drives the one-time full re-backfill.</summary>
        Task<int> GetBackfillVersionAsync(CancellationToken cancellationToken = default);

        /// <summary>Advance the durable backfill marker to <paramref name="version"/>
        /// (single-row upsert) after a full re-backfill completes.</summary>
        Task SetBackfillVersionAsync(int version, CancellationToken cancellationToken = default);

        /// <summary>Issue #138 (PR-C2): bound the rollup table — single SQL DELETE
        /// of daily rows whose Day is older than <paramref name="days"/>. Returns
        /// rows removed.</summary>
        Task<int> BulkDeleteOlderThanDaysAsync(int days, CancellationToken cancellationToken = default);
    }

    public class DailyStatusRollupRepository(SuperStatusDb context) : IDailyStatusRollupRepository
    {
        public async Task<List<DailyStatusRollup>> GetSinceAsync(long statusCheckId, DateTime sinceDayUtc, CancellationToken cancellationToken = default)
        {
            DateTime since = sinceDayUtc.Date;
            return await context.DailyStatusRollupSet
                .AsNoTracking()
                .Where(r => r.StatusCheckId == statusCheckId && r.Day >= since)
                .ToListAsync(cancellationToken);
        }

        public async Task<List<DailyStatusRollup>> GetSinceForChecksAsync(IReadOnlyCollection<long> statusCheckIds, DateTime sinceDayUtc, CancellationToken cancellationToken = default)
        {
            if (statusCheckIds.Count == 0) return new List<DailyStatusRollup>();
            DateTime since = sinceDayUtc.Date;
            return await context.DailyStatusRollupSet
                .AsNoTracking()
                .Where(r => statusCheckIds.Contains(r.StatusCheckId) && r.Day >= since)
                .ToListAsync(cancellationToken);
        }

        public async Task UpsertAsync(long statusCheckId, DateTime dayUtc, int total, int down, int degraded, int unreachable = 0, CancellationToken cancellationToken = default)
        {
            DateTime day = dayUtc.Date;
            var existing = await context.DailyStatusRollupSet
                .FirstOrDefaultAsync(r => r.StatusCheckId == statusCheckId && r.Day == day, cancellationToken);
            if (existing is null)
            {
                context.DailyStatusRollupSet.Add(new DailyStatusRollup
                {
                    StatusCheckId = statusCheckId, Day = day, Total = total, Down = down, Degraded = degraded, Unreachable = unreachable,
                });
            }
            else
            {
                existing.Total = total;
                existing.Down = down;
                existing.Degraded = degraded;
                existing.Unreachable = unreachable;
            }
            await context.SaveChangesAsync(cancellationToken);
        }

        public Task<bool> AnyAsync(CancellationToken cancellationToken = default)
            => context.DailyStatusRollupSet.AnyAsync(cancellationToken);

        public async Task<int> GetBackfillVersionAsync(CancellationToken cancellationToken = default)
        {
            var row = await context.RollupMaintenanceStateSet
                .AsNoTracking()
                .OrderByDescending(r => r.BackfillVersion)
                .FirstOrDefaultAsync(cancellationToken);
            return row?.BackfillVersion ?? 0;
        }

        public async Task SetBackfillVersionAsync(int version, CancellationToken cancellationToken = default)
        {
            var row = await context.RollupMaintenanceStateSet.FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                context.RollupMaintenanceStateSet.Add(new RollupMaintenanceState { BackfillVersion = version, UpdatedUtc = DateTime.UtcNow });
            }
            else
            {
                row.BackfillVersion = version;
                row.UpdatedUtc = DateTime.UtcNow;
            }
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task<int> BulkDeleteOlderThanDaysAsync(int days, CancellationToken cancellationToken = default)
        {
            DateTime cutoff = DateTime.UtcNow.Date.AddDays(-days);
            return await context.DailyStatusRollupSet.Where(r => r.Day < cutoff).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
