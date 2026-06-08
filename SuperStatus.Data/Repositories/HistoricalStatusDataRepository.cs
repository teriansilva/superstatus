using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public interface IHistoricalStatusDataRepository : IRepository<HistoricalStatusData>
    {
        Task<HistoricalStatusData?> GetMostRecentHistoricalStatusData(long statusCheckId);

        /// <summary>
        /// Issue #136: per-day state tally for one check since <paramref name="sinceUtc"/>,
        /// computed DB-side (one <c>GROUP BY date</c>, conditional counts) and
        /// returned as a small <see cref="DailyStateRollup"/> projection — never
        /// materializes the raw tick window. Plans on the (StatusCheckId,
        /// TimeOfCheckUTC) composite index. Powers the dashboard 30-day strip /
        /// uptime without re-scanning millions of rows.
        /// </summary>
        Task<List<DailyStateRollup>> GetDailyStateRollupAsync(long statusCheckId, int expectedStatusCode, long expectedResponseTimeMs, DateTime sinceUtc, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #138 (PR-B): the recent-window daily tally for MANY checks in
        /// one query — joins each tick to its <see cref="StatusCheck"/> so the
        /// down/degraded classification uses that check's own thresholds, then
        /// <c>GROUP BY (StatusCheckId, date)</c>. Replaces the per-check
        /// <see cref="GetDailyStateRollupAsync"/> calls in the dashboard's serial
        /// loop. Only the in-retention recent days are read here; older days come
        /// from the persisted rollup table.
        /// </summary>
        Task<List<CheckDailyStateRollup>> GetRecentDailyStateForChecksAsync(IReadOnlyCollection<long> statusCheckIds, DateTime sinceUtc, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #138 (PR-B): the most-recent tick per check for a set of checks,
        /// in two indexed queries (max-time-per-check, then the rows at those
        /// times) rather than N point look-ups in a loop. Provider-agnostic — no
        /// reliance on EF translating "first row per group". Checks with no
        /// history are absent from the result.
        /// </summary>
        Task<IReadOnlyDictionary<long, HistoricalStatusData>> GetMostRecentForChecksAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #138 (PR-B): response times (ms) of successful ticks since
        /// <paramref name="sinceUtc"/> across MANY checks, in one query — feeds
        /// the dashboard hero avg / p95. Failed ticks are excluded (no meaningful
        /// wall-clock latency), matching the former per-check last-hour scan.
        /// </summary>
        Task<List<long>> GetLatencySamplesSinceAsync(IReadOnlyCollection<long> statusCheckIds, DateTime sinceUtc, CancellationToken cancellationToken = default);
        Task<IPagedResult<HistoricalStatusData>> GetHistoricalStatusDataSetForStatusCheckId(long statusCheckId, int page = 1, int pageSize = 0);
        Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataSetForDaysGroupedByDays(long statusCheckId, int timeRangeInDays);
        Task<List<HistoricalStatusData>> GetHistoricalStatusDataOlderThanXDays(int days);

        /// <summary>
        /// Most-recent N ticks for one status check, newest first. Plans against
        /// the (StatusCheckId ASC, TimeOfCheckUTC DESC) composite index from #79.
        /// </summary>
        Task<List<HistoricalStatusData>> GetRecentTicks(long statusCheckId, int count);

        /// <summary>
        /// Bulk-deletes rows whose TimeOfCheckUTC is older than <paramref name="days"/>
        /// via a single SQL DELETE. Returns the number of rows removed.
        /// </summary>
        Task<int> BulkDeleteOlderThanXDaysAsync(int days, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #138 (PR-C2): the small-footprint raw-tick prune — deletes rows
        /// older than <paramref name="hours"/> (default retention ~72 h) in bounded
        /// BATCHES of <paramref name="batchSize"/>. Hours rather than days because
        /// the raw window is sub-day scale now; older history lives in the daily
        /// rollups. Returns rows removed.
        ///
        /// Batched because the FIRST prune on an upgraded instance clears the whole
        /// 30-day → 72 h backlog (millions of rows + FK cascade), which as one
        /// statement blew the 30 s command timeout and starved concurrent
        /// status-check inserts on the locked table (observed on staging). Each
        /// batch is a short, indexed transaction that releases locks between
        /// rounds; ongoing prunes (small) complete in one batch.
        /// </summary>
        Task<int> BulkDeleteRawOlderThanHoursAsync(int hours, int batchSize = 20_000, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #82: the most-recent tick time per status check, in ONE grouped
        /// query (no N+1), for the scheduler's per-check due calculation. Checks
        /// with no history are absent from the result (the scheduler treats a
        /// missing entry as "never run → due"). Plans against the
        /// (StatusCheckId, TimeOfCheckUTC) composite index from #79.
        /// </summary>
        Task<IReadOnlyDictionary<long, DateTime>> GetLastCheckTimesAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default);
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

        public async Task<List<DailyStateRollup>> GetDailyStateRollupAsync(long statusCheckId, int expectedStatusCode, long expectedResponseTimeMs, DateTime sinceUtc, CancellationToken cancellationToken = default)
        {
            // One GROUP BY date per check. EF translates the conditional Count
            // to COUNT(*) FILTER (WHERE …) on Npgsql / COUNT(CASE …) on SQLite —
            // no rows leave the DB except the ~30 daily tallies.
            return await DbSet
                .Where(x => x.StatusCheckId == statusCheckId && x.TimeOfCheckUTC >= sinceUtc)
                .GroupBy(x => x.TimeOfCheckUTC.Date)
                .Select(g => new DailyStateRollup(
                    g.Key,
                    g.Count(),
                    g.Count(x => x.CheckFailed || x.HttpStatusCode != expectedStatusCode),
                    g.Count(x => !x.CheckFailed && x.HttpStatusCode == expectedStatusCode && x.ResponseTimeInMs > expectedResponseTimeMs),
                    g.Count(x => x.CheckFailed)))
                .ToListAsync(cancellationToken);
        }

        public async Task<List<CheckDailyStateRollup>> GetRecentDailyStateForChecksAsync(IReadOnlyCollection<long> statusCheckIds, DateTime sinceUtc, CancellationToken cancellationToken = default)
        {
            if (statusCheckIds.Count == 0) return new List<CheckDailyStateRollup>();
            // One join + GROUP BY (StatusCheckId, date) for every check at once.
            // Joining to StatusCheckSet lets the down/degraded classification use
            // each check's own ExpectedStatusCode / ExpectedResponseTimeInMs, so
            // the result is identical to the per-check GetDailyStateRollupAsync —
            // just batched. EF emits COUNT(*) FILTER (Npgsql) / COUNT(CASE …)
            // (SQLite), plans on the (StatusCheckId, TimeOfCheckUTC) composite.
            return await (
                from h in DbSet
                join c in Context.StatusCheckSet on h.StatusCheckId equals c.Id
                where statusCheckIds.Contains(h.StatusCheckId) && h.TimeOfCheckUTC >= sinceUtc
                group new { h, c } by new { h.StatusCheckId, Day = h.TimeOfCheckUTC.Date } into g
                select new CheckDailyStateRollup(
                    g.Key.StatusCheckId,
                    g.Key.Day,
                    g.Count(),
                    g.Count(x => x.h.CheckFailed || x.h.HttpStatusCode != x.c.ExpectedStatusCode),
                    g.Count(x => !x.h.CheckFailed && x.h.HttpStatusCode == x.c.ExpectedStatusCode && x.h.ResponseTimeInMs > x.c.ExpectedResponseTimeInMs),
                    g.Count(x => x.h.CheckFailed)))
                .ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyDictionary<long, HistoricalStatusData>> GetMostRecentForChecksAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<long, HistoricalStatusData>();
            if (statusCheckIds.Count == 0) return result;

            // Per-check index seek (ORDER BY TimeOfCheckUTC DESC LIMIT 1) on the
            // (StatusCheckId, TimeOfCheckUTC DESC) composite — ~3 ms each, ≈100 ms
            // for ~30 checks. This is a CHEAP N+1: every query returns exactly one
            // row via the index, no scan. The earlier set-based GROUP BY-max
            // (max(TimeOfCheckUTC) GROUP BY StatusCheckId) measured 845 ms on
            // staging because Postgres parallel-seq-scanned all 3.4 M raw rows to
            // compute the maxima — the index can't serve a grouped aggregate, so
            // "batching" was a pessimization here. (#141 follow-up; verified by
            // EXPLAIN ANALYZE.) DbContext is single-threaded, so the seeks run
            // sequentially — still ~100 ms total.
            foreach (var id in statusCheckIds)
            {
                var row = await DbSet
                    .Where(x => x.StatusCheckId == id)
                    .OrderByDescending(x => x.TimeOfCheckUTC)
                    .FirstOrDefaultAsync(cancellationToken);
                if (row is not null) result[id] = row;
            }
            return result;
        }

        public async Task<List<long>> GetLatencySamplesSinceAsync(IReadOnlyCollection<long> statusCheckIds, DateTime sinceUtc, CancellationToken cancellationToken = default)
        {
            if (statusCheckIds.Count == 0) return new List<long>();
            return await DbSet
                .Where(x => statusCheckIds.Contains(x.StatusCheckId) && x.TimeOfCheckUTC >= sinceUtc && !x.CheckFailed)
                .Select(x => x.ResponseTimeInMs)
                .ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyDictionary<long, DateTime>> GetLastCheckTimesAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default)
        {
            if (statusCheckIds.Count == 0) return new Dictionary<long, DateTime>();
            var rows = await DbSet
                .Where(x => statusCheckIds.Contains(x.StatusCheckId))
                .GroupBy(x => x.StatusCheckId)
                .Select(g => new { StatusCheckId = g.Key, Last = g.Max(x => x.TimeOfCheckUTC) })
                .ToListAsync(cancellationToken);
            return rows.ToDictionary(r => r.StatusCheckId, r => r.Last);
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

        public async Task<List<HistoricalStatusData>> GetHistoricalStatusDataOlderThanXDays(int days)
        {
            return await DbSet.Where(x => x.TimeOfCheckUTC < DateTime.UtcNow.AddDays(-days)).ToListAsync();
        }

        public async Task<List<HistoricalStatusData>> GetRecentTicks(long statusCheckId, int count)
        {
            int safeCount = Math.Clamp(count, 1, 500);
            return await DbSet
                .Where(x => x.StatusCheckId == statusCheckId)
                .OrderByDescending(x => x.TimeOfCheckUTC)
                .Take(safeCount)
                .ToListAsync();
        }

        public async Task<int> BulkDeleteOlderThanXDaysAsync(int days, CancellationToken cancellationToken = default)
        {
            // Issue #80. The previous cleanup path materialized every expired
            // row into memory then asked the change tracker to delete each,
            // triggering EF's ClientCascade — one DELETE per parent + one per
            // related HistoricalStatusAction. ExecuteDeleteAsync emits a
            // single SQL DELETE that PostgreSQL plans against the new
            // IX_HistoricalStatusDataSet_TimeOfCheckUTC index, and the
            // cascade flip in SuperStatusDb.OnModelCreating (ClientCascade ->
            // Cascade) means HistoricalStatusAction rows are removed by
            // PostgreSQL via the FK ON DELETE CASCADE instead of EF.
            DateTime cutoff = DateTime.UtcNow.AddDays(-days);
            return await DbSet.Where(x => x.TimeOfCheckUTC < cutoff).ExecuteDeleteAsync(cancellationToken);
        }

        public async Task<int> BulkDeleteRawOlderThanHoursAsync(int hours, int batchSize = 20_000, CancellationToken cancellationToken = default)
        {
            // Batched delete: pick a bounded slice of the oldest expired Ids (one
            // indexed scan on IX_..._TimeOfCheckUTC), then ExecuteDeleteAsync by PK
            // for that slice — a short transaction whose FK cascade
            // (HistoricalStatusAction, #80) is bounded, so it never trips the 30 s
            // command timeout or holds the table locked while millions of rows are
            // removed. Repeats until no expired rows remain. (EF can't combine
            // Take() with ExecuteDelete, hence select-ids-then-delete-by-id.)
            int safeBatch = Math.Clamp(batchSize, 1, 100_000);
            DateTime cutoff = DateTime.UtcNow.AddHours(-hours);
            int total = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var ids = await DbSet
                    .Where(x => x.TimeOfCheckUTC < cutoff)
                    .OrderBy(x => x.TimeOfCheckUTC)
                    .Take(safeBatch)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);
                if (ids.Count == 0) break;
                total += await DbSet.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
                if (ids.Count < safeBatch) break;   // last (partial) batch
            }
            return total;
        }

    }
}
