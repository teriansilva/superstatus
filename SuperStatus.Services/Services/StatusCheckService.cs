using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Data.Extensions;
using SuperStatus.Services.Http;
using SuperStatus.Services.Scheduling;
using SuperStatus.Services.Telemetry;
using System.Diagnostics;

namespace SuperStatus.Services.Services
{
    public interface IStatusCheckService
    {
        Task<IPagedResult<StatusCheck>> GetStatusCheckSet(int page = 1, int pageSize = 0);
        Task<StatusCheck?> GetStatusCheck(long StatusCheckId);

        /// <summary>
        /// Issue #107 Phase 2: newest-first webhook execution log for the admin
        /// audit UI. <paramref name="failuresOnly"/> narrows to actual wire
        /// failures (NonSuccess/Timeout/TransportFailure).
        /// </summary>
        Task<List<WebhookExecutionLogViewModel>> GetRecentWebhookLogAsync(int count, bool failuresOnly, CancellationToken cancellationToken = default);

        /// <summary>
        /// #291 Phase B: one operator-triggered wire attempt against a webhook
        /// target, through the same executor path real dispatch uses. Returns
        /// the wire result; writes NO execution-log row (a test has no
        /// triggering check and StatusCheckId is a required FK — no schema
        /// change in this phase).
        /// </summary>
        Task<WebhookTestFireResult> TestFireWebhookAsync(Webhook webhook, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #82: most-recent tick time per check (one grouped query), for
        /// the scheduler's per-check due calculation. Missing entry → never run.
        /// </summary>
        Task<IReadOnlyDictionary<long, DateTime>> GetLastCheckTimesAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default);
        Task<IPagedResult<StatusCheckViewModel>> GetStatusCheckViewModelSet(int page = 1, int pageSize = 0);
        Task<HistoricalStatusDataViewModel?> GetMostRecentHistoricalStatusData(long statusCheckId);
        Task<IPagedResult<HistoricalStatusData>> GetPagedHistoricalStatusDataForStatusCheckId(long statusCheckId, int page, int pageSize = 0);
        Task<List<HistoricalStatusData>> GetRecentTicks(long statusCheckId, int count);
        Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataForStatusCheckIdByDays(long statusCheckId, int timeRangeInDays);
        Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForRecentTimeRange(long statusCheckId, int timeRangeInDays);

        /// <summary>Issue #226: the per-day overview for ALL checks in one batched
        /// read (collapses the dashboard's N+1 — formerly one query per card). Flat
        /// list of cells grouped by check (ascending by date within each), so the
        /// caller can group by <c>StatusCheckId</c>.</summary>
        Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForAllChecks(int timeRangeInDays);

        /// <summary>Issue #201: compact per-day breakdown for one uptime-strip cell
        /// (lazy hover detail), via the same rollup-aware boundary as the strip.
        /// Returns null when the check doesn't exist; a no-sample day → Status "gap".</summary>
        Task<DayDetailViewModel?> GetDayDetailAsync(long statusCheckId, DateOnly date, CancellationToken cancellationToken = default);
        Task<DashboardSummaryViewModel> GetDashboardSummaryAsync(int incidents30dCount, CancellationToken cancellationToken = default);
        Task<HistoricalStatusData> ExecuteStatusCheck(StatusCheck statusCheck);
        Task<HistoricalStatusData> SaveStatusCheckResult(HistoricalStatusData statusCheckResult);
        Task<HistoricalStatusAction?> RunPostStatusCheckTasks(StatusCheck statusCheck, HistoricalStatusData statusCheckResult);
        Task<HistoricalStatusAction> SaveStatusCheckAction(HistoricalStatusAction action);
        Task<StatusCheck> AddOrUpdateStatusCheck(StatusCheckViewModelBase statusCheck);

        /// <summary>
        /// Issue #105. Operator-triggered single-tick run. Runs the same
        /// pipeline the scheduler runs (Execute → Save → RunPostTasks → SaveAction)
        /// so #75's exactly-once contract is preserved. Returns the persisted
        /// historical row. 404-style behaviour is via the caller — null
        /// statusCheck input throws (the API endpoint resolves it first).
        /// </summary>
        Task<HistoricalStatusDataViewModel> RunCheckNowAsync(long statusCheckId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #105. Toggle Enabled flag. The scheduler honours this on its
        /// next tick. Manual /run-now is *not* gated by Enabled — operators
        /// can still smoke-test a disabled check.
        /// </summary>
        Task<StatusCheck> SetEnabledAsync(long statusCheckId, bool enabled, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #164. Operator-triggered hard delete of a status check. The
        /// check's dependent rows (historical ticks, daily rollups, webhook
        /// logs) are removed by the database's ON DELETE CASCADE FKs. Returns
        /// false if no check with that id exists (→ 404 at the API).
        /// </summary>
        Task<bool> DeleteStatusCheckAsync(long statusCheckId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #83: fold a just-saved result's outcome into the check's
        /// consecutive-failure counter — reset to 0 on a healthy (NoFail)
        /// result, increment on a failure. Persists ONLY when the value
        /// actually changes, so a steady-healthy check is not re-written every
        /// tick. The caller passes the same tracked <paramref name="check"/> it
        /// executed, in the same scope, right after SaveStatusCheckResult.
        /// </summary>
        Task RecordCheckOutcomeAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #138: (re)compute and upsert the persisted daily rollups for
        /// the last <paramref name="daysBack"/> days, per check. The rollup job
        /// calls this each tick for the recent days (cheap); a one-time backfill
        /// passes the full window. Idempotent.
        /// </summary>
        Task RefreshDailyRollupsAsync(int daysBack, CancellationToken cancellationToken = default);
    }
    public class StatusCheckService(IStatusCheckRepository statusCheckRepository, IHistoricalStatusDataRepository historicalStatusDataRepository, IHistoricalStatusActionRepository historicalStatusActionRepository, IWebhookExecutionLogRepository webhookExecutionLogRepository, IDailyStatusRollupRepository dailyStatusRollupRepository, IHttpClientFactory httpClientFactory, ILogger<StatusCheckService> logger, IAutoIncidentCoordinator? autoIncidentCoordinator = null, IStatusCheckLinkRepository? statusCheckLinkRepository = null) : IStatusCheckService
    {
        public async Task RefreshDailyRollupsAsync(int daysBack, CancellationToken cancellationToken = default)
        {
            DateTime since = DateTime.UtcNow.Date.AddDays(-(Math.Max(1, daysBack) - 1));
            var checks = (await statusCheckRepository.GetStatusCheckSet()).Results;
            foreach (var check in checks)
            {
                // #293: the slow threshold comes from the linked SLA (the repo
                // eager-loads it); the legacy ms column is no longer read here.
                var rollup = await historicalStatusDataRepository.GetDailyStateRollupAsync(
                    check.Id, check.ExpectedStatusCode, GetSlowThresholdMs(check), since, cancellationToken);
                foreach (var day in rollup)
                {
                    await dailyStatusRollupRepository.UpsertAsync(check.Id, day.Day, day.Total, day.Down, day.Degraded, day.Unreachable, cancellationToken);
                }
            }
        }
        public async Task<IPagedResult<StatusCheck>> GetStatusCheckSet(int page = 1, int pageSize = 0)
        {
            return await statusCheckRepository.GetStatusCheckSet(page, pageSize);
        }
        public async Task<StatusCheck?> GetStatusCheck(long StatusCheckId)
        {
            return await statusCheckRepository.GetStatusCheckById(StatusCheckId);
        }
        public async Task<List<WebhookExecutionLogViewModel>> GetRecentWebhookLogAsync(int count, bool failuresOnly, CancellationToken cancellationToken = default)
        {
            var rows = await webhookExecutionLogRepository.GetRecentWithCheckAsync(count, failuresOnly, cancellationToken);
            return rows.Select(r => new WebhookExecutionLogViewModel(r)).ToList();
        }
        public async Task<IReadOnlyDictionary<long, DateTime>> GetLastCheckTimesAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default)
        {
            return await historicalStatusDataRepository.GetLastCheckTimesAsync(statusCheckIds, cancellationToken);
        }
        public async Task<IPagedResult<StatusCheckViewModel>> GetStatusCheckViewModelSet(int page = 1, int pageSize = 0)
        {
            IPagedResult<StatusCheck> statusCheckSet = await GetStatusCheckSet(page, pageSize);
            IPagedResult<StatusCheckViewModel> result = await statusCheckSet.MapToAsync(async x => new StatusCheckViewModel(x, await GetMostRecentHistoricalStatusData(x.Id)));

            // #291: round-trip the linked target ids on the read side (read-only
            // props — the edit endpoint ignores them). Two batched queries, no N+1.
            if (statusCheckLinkRepository is not null && result.Results.Count > 0)
            {
                var ids = result.Results.Select(vm => vm.Id).ToList();
                var linkedWebhooks = await statusCheckLinkRepository.GetLinkedWebhookIdsAsync(ids);
                var linkedProfiles = await statusCheckLinkRepository.GetLinkedAlertProfileIdsAsync(ids);
                foreach (var vm in result.Results)
                {
                    vm.LinkedWebhookIds = linkedWebhooks.TryGetValue(vm.Id, out var w) ? w : new List<long>();
                    vm.LinkedAlertProfileIds = linkedProfiles.TryGetValue(vm.Id, out var a) ? a : new List<long>();
                }
            }
            return result;
        }
        public async Task<HistoricalStatusDataViewModel?> GetMostRecentHistoricalStatusData(long statusCheckId)
        {
            HistoricalStatusData? mostRecentHistoricalStatusData = await historicalStatusDataRepository.GetMostRecentHistoricalStatusData(statusCheckId);
            StatusCheck? statusCheck = await GetStatusCheck(statusCheckId);
            return mostRecentHistoricalStatusData != null ? new HistoricalStatusDataViewModel(mostRecentHistoricalStatusData) : null;
        }
        public async Task<IPagedResult<HistoricalStatusData>> GetPagedHistoricalStatusDataForStatusCheckId(long statusCheckId, int page, int pageSize = 0)
        {
            return await historicalStatusDataRepository.GetHistoricalStatusDataSetForStatusCheckId(statusCheckId, page, pageSize);
        }
        public async Task<List<HistoricalStatusData>> GetRecentTicks(long statusCheckId, int count)
        {
            return await historicalStatusDataRepository.GetRecentTicks(statusCheckId, count);
        }
        public async Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataForStatusCheckIdByDays(long statusCheckId, int timeRangeInDays)
        {
            return await historicalStatusDataRepository.GetHistoricalStatusDataSetForDaysGroupedByDays(statusCheckId, timeRangeInDays);
        }
        public async Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForRecentTimeRange(long statusCheckId, int timeRangeInDays)
        {
            DateTime now = DateTime.UtcNow;
            StatusCheck? statusCheck = await GetStatusCheck(statusCheckId);

            if (statusCheck == null)
            {
                logger.LogInformation("Failed to find status check with id {StatusCheckId}", statusCheckId);
                throw new Exception($"Failed to find status check with id {statusCheckId}");
            }

            int days = Math.Max(1, timeRangeInDays);
            DateTime windowStart = now.Date.AddDays(-(days - 1));

            // #138 (PR-C1): the per-day overview reads through THE canonical
            // rollup-aware boundary (today on-the-fly, prior days from the rollup
            // table) instead of scanning raw — so it survives the ~72 h raw prune
            // (PR-C2) and stays cheap. The day's up/degraded/down colour is exact;
            // for days served from the rollup the unreachable-vs-bad-status split
            // is approximate (bad-status ≈ Down − Unreachable), and the slow count
            // is the rollup's Degraded (slow among otherwise-healthy ticks) rather
            // than the old "any slow tick" — same cell colour, marginally different
            // tooltip number for old days.
            var byDay = (await BuildDailyStateByCheckAsync(new[] { statusCheck.Id }, windowStart, now, CancellationToken.None))
                .GetValueOrDefault(statusCheck.Id) ?? new Dictionary<DateTime, (int Total, int Down, int Degraded, int Unreachable)>();

            var set = new List<HistoricalStatusDataOverviewChartViewModel>(days);
            for (int i = 0; i < days; i++)
            {
                DateTime day = windowStart.AddDays(i);
                byDay.TryGetValue(day.Date, out var d);   // #200: absent day → default (0,0,0,0); Total 0 = no samples → strip renders grey "gap" (not up)
                int unreachable = d.Unreachable;
                int badStatus = Math.Max(0, d.Down - d.Unreachable);
                int slow = d.Degraded;
                set.Add(new HistoricalStatusDataOverviewChartViewModel(
                    statusCheck.Id, DateOnly.FromDateTime(day), badStatus, slow, unreachable, d.Total));
            }

            return set;   // ascending by date
        }

        public async Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForAllChecks(int timeRangeInDays)
        {
            // #226: one batched read for the whole dashboard.
            // BuildDailyStateByCheckAsync issues exactly two batched queries for ALL
            // checks (rollups + today), so this is 3 DB round-trips total regardless of
            // check count — versus the former N+1 (one GetHistoricalStatusData per card,
            // which exhausted the connection pool with many checks).
            int days = Math.Max(1, timeRangeInDays);
            DateTime now = DateTime.UtcNow;
            DateTime windowStart = now.Date.AddDays(-(days - 1));

            var checks = (await statusCheckRepository.GetStatusCheckSet()).Results.ToList();
            var checkIds = checks.Select(c => c.Id).ToList();

            var dailyStateByCheck = await BuildDailyStateByCheckAsync(checkIds, windowStart, now, CancellationToken.None);

            var set = new List<HistoricalStatusDataOverviewChartViewModel>(checks.Count * days);
            foreach (StatusCheck sc in checks)
            {
                var byDay = dailyStateByCheck.TryGetValue(sc.Id, out var m)
                    ? m : new Dictionary<DateTime, (int Total, int Down, int Degraded, int Unreachable)>();

                for (int i = 0; i < days; i++)
                {
                    DateTime day = windowStart.AddDays(i);
                    byDay.TryGetValue(day.Date, out var d);   // absent day → (0,0,0,0) → "gap"
                    int unreachable = d.Unreachable;
                    int badStatus = Math.Max(0, d.Down - d.Unreachable);
                    int slow = d.Degraded;
                    set.Add(new HistoricalStatusDataOverviewChartViewModel(
                        sc.Id, DateOnly.FromDateTime(day), badStatus, slow, unreachable, d.Total));
                }
            }

            return set;   // grouped by check (insertion order), ascending by date within each
        }

        public async Task<DayDetailViewModel?> GetDayDetailAsync(long statusCheckId, DateOnly date, CancellationToken cancellationToken = default)
        {
            // #201: null (→ 404) when the check doesn't exist.
            StatusCheck? statusCheck = await GetStatusCheck(statusCheckId);
            if (statusCheck is null) return null;

            DateTime now = DateTime.UtcNow;
            DateTime dayUtc = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            // A future day (or before the rollup window) simply has no samples → gap.
            var byDay = (await BuildDailyStateByCheckAsync(new[] { statusCheck.Id }, dayUtc, now, cancellationToken))
                .GetValueOrDefault(statusCheck.Id) ?? new Dictionary<DateTime, (int Total, int Down, int Degraded, int Unreachable)>();

            byDay.TryGetValue(dayUtc.Date, out var d);   // absent day → (0,0,0,0): no samples → gap
            int up = Math.Max(0, d.Total - d.Down - d.Degraded);
            // #293 Phase B: the tooltip verdict classifies via the linked SLA so
            // it matches the strip cell (raw counts below stay untouched). The
            // tuple's Down already includes Unreachable.
            Sla? sla = statusCheck.Sla;
            string status = SlaDayClassifier.Classify(d.Total, d.Down, d.Degraded, sla);

            return new DayDetailViewModel
            {
                StatusCheckId = statusCheck.Id,
                Date = date,
                Status = status,
                Total = d.Total,
                Up = up,
                Degraded = d.Degraded,
                Down = d.Down,
                Unreachable = d.Unreachable,
                UptimePct = d.Total == 0 ? 0 : Math.Round(up * 100.0 / d.Total, 1),
                // #293: surface the SLA target ONLY when it deviates from the
                // behavior-identical 100/100, so default instances keep today's
                // exact popover wording.
                SlaTargetPercent = sla is not null && (sla.TargetUptimePercent != 100 || sla.CriticalUptimePercent != 100)
                    ? sla.TargetUptimePercent : null,
            };
        }
        /// <summary>
        /// Aggregated dashboard summary (issue #104). One round-trip backing
        /// the Home hero panel + per-service 30-day uptime strips. Caller
        /// passes the incidents-in-window count so the service stays
        /// repository-agnostic.
        /// </summary>
        public async Task<DashboardSummaryViewModel> GetDashboardSummaryAsync(int incidents30dCount, CancellationToken cancellationToken = default)
        {
            // 30-day uptime window. v1 keeps the latency window narrower so
            // a slow upstream from a week ago doesn't dominate the hero
            // p95 reading; documented in the issue body.
            DateTime now = DateTime.UtcNow;
            DateTime uptimeWindowStart = now.AddDays(-29).Date;   // 30 calendar days incl. today
            DateTime latencyWindowStart = now.AddHours(-1);

            var checks = (await statusCheckRepository.GetStatusCheckSet()).Results.ToList();
            var checkIds = checks.Select(c => c.Id).ToList();

            // #138 (PR-B): the former per-check serial loop (4 queries × N checks)
            // is collapsed into a handful of set-based queries so the COLD path is
            // sub-second regardless of check count — the acceptance #136's cache
            // only masked. (1) most-recent tick per check, (2) last-hour latency
            // samples across all checks, (3) the canonical rollup-aware per-day
            // state for every check (old days from the rollup table, recent days
            // from raw). Nothing aggregates over the full raw window on the request.
            var mostRecentByCheck = await historicalStatusDataRepository.GetMostRecentForChecksAsync(checkIds, cancellationToken);
            var latencies = (await historicalStatusDataRepository.GetLatencySamplesSinceAsync(checkIds, latencyWindowStart, cancellationToken))
                .Select(ms => (int)ms).ToList();
            var dailyStateByCheck = await BuildDailyStateByCheckAsync(checkIds, uptimeWindowStart, now, cancellationToken);

            int up = 0, degraded = 0, down = 0;
            var perService = new List<DashboardPerServiceViewModel>(checks.Count);
            long totalTicks = 0;
            long okTicks = 0;

            foreach (var check in checks)
            {
                mostRecentByCheck.TryGetValue(check.Id, out var mostRecent);
                string currentState = MostRecentState(check, mostRecent);
                switch (currentState)
                {
                    case "up": up++; break;
                    case "degraded": degraded++; break;
                    case "down": down++; break;
                }

                // 30-day per-service strip. Each day classifies against the
                // check's SLA (#293 — 100/100 default == worst-of-tick); days
                // with no samples → "gap" (never silently "up").
                var rollupByDay = dailyStateByCheck.TryGetValue(check.Id, out var d)
                    ? d : new Dictionary<DateTime, (int Total, int Down, int Degraded, int Unreachable)>();
                var strip = new string[30];
                for (int i = 0; i < 30; i++)
                {
                    var dayKey = uptimeWindowStart.AddDays(i).Date;
                    if (rollupByDay.TryGetValue(dayKey, out var day) && day.Total > 0)
                    {
                        // #293 Phase B: classify via the check's linked SLA (the
                        // repo eager-loads it; null → behavior-identical 100/100).
                        // The tuple's Down already includes Unreachable.
                        strip[i] = SlaDayClassifier.Classify(day.Total, day.Down, day.Degraded, check.Sla);
                        // Tick-level uptime, same semantic as the day tooltip's
                        // UptimePct (ok = Total − Down − Degraded). Counting green
                        // DAYS here meant one blip tick anywhere in a day zeroed
                        // that day's contribution — a near-perfect fleet could
                        // read absurdly low (a 2-day-old check with one blip per
                        // day read 0.0%).
                        totalTicks += day.Total;
                        okTicks += Math.Max(0, day.Total - day.Down - day.Degraded);
                    }
                    else
                    {
                        strip[i] = "gap";
                    }
                }
                perService.Add(new DashboardPerServiceViewModel(
                    StatusCheckId: check.Id,
                    Title: check.Title,
                    CurrentState: currentState,
                    Uptime30d: strip));
            }

            int? avg = latencies.Count > 0 ? (int)latencies.Average() : null;
            int? p95 = latencies.Count > 0 ? Percentile(latencies, 0.95) : null;
            double uptime30dPct = totalTicks > 0 ? (okTicks * 100.0 / totalTicks) : 0;

            string overall = ComputeOverall(up, degraded, down, incidents30dCount);

            return new DashboardSummaryViewModel(
                Services: new DashboardServiceCountsViewModel(up, degraded, down, checks.Count),
                LatencyMs: new DashboardLatencyViewModel(avg, p95),
                Uptime30dPct: uptime30dPct,
                Incidents30d: incidents30dCount,
                PerService: perService,
                Overall: overall,
                GeneratedUtc: now);
        }

        /// <summary>
        /// Issue #138 (PR-B): THE single canonical read boundary. For a set of
        /// checks it returns, per check, a per-UTC-day state tally over
        /// [<paramref name="windowStartUtc"/>, now]. <b>TODAY</b> is aggregated
        /// on-the-fly from raw (always live); <b>every prior day</b> is read from
        /// the persisted <c>DailyStatusRollup</c> table. The summary and per-day
        /// strip reads both call this — neither re-invents the cutoff. Two batched queries total.
        ///
        /// Boundary is <c>today (now.Date)</c>, NOT the raw-retention window
        /// (#141 follow-up): aggregating the full ~72 h raw window on-the-fly cost
        /// 1.1 s on staging (563 k rows) vs 148 ms for today alone (97 k rows) —
        /// EXPLAIN-verified. The rollup job (#84/PR-A) refreshes today + yesterday
        /// every cleanup tick, so a prior day's rollup is current to within one
        /// tick; reading it (instead of re-scanning raw) is both correct and an
        /// order of magnitude cheaper. Reading only today from raw is always safe:
        /// <c>RawTickRetentionHours</c> (≥ 48 h) guarantees today's raw is never
        /// pruned. Every day is covered exactly once — today from raw overrides any
        /// persisted today-rollup; prior days from the rollup table.
        /// </summary>
        private async Task<Dictionary<long, Dictionary<DateTime, (int Total, int Down, int Degraded, int Unreachable)>>>
            BuildDailyStateByCheckAsync(IReadOnlyCollection<long> checkIds, DateTime windowStartUtc, DateTime now, CancellationToken cancellationToken)
        {
            var result = new Dictionary<long, Dictionary<DateTime, (int Total, int Down, int Degraded, int Unreachable)>>();
            foreach (var id in checkIds) result[id] = new Dictionary<DateTime, (int, int, int, int)>();
            if (checkIds.Count == 0) return result;

            DateTime windowStartDay = windowStartUtc.Date;
            DateTime today = now.Date;

            // PRIOR days (< today): persisted rollups, one batched query.
            var persisted = await dailyStatusRollupRepository.GetSinceForChecksAsync(checkIds, windowStartDay, cancellationToken);
            foreach (var r in persisted)
            {
                DateTime day = r.Day.Date;
                if (day >= windowStartDay && day < today && result.TryGetValue(r.StatusCheckId, out var m))
                {
                    m[day] = (r.Total, r.Down, r.Degraded, r.Unreachable);
                }
            }

            // TODAY: aggregated on-the-fly from raw (one batched query), overriding
            // any persisted today-rollup so the current day's cell is never stale.
            var recent = await historicalStatusDataRepository.GetRecentDailyStateForChecksAsync(checkIds, today, cancellationToken);
            foreach (var r in recent)
            {
                if (result.TryGetValue(r.StatusCheckId, out var m))
                {
                    m[r.Day.Date] = (r.Total, r.Down, r.Degraded, r.Unreachable);
                }
            }

            return result;
        }

        /// <summary>
        /// FailType → state vocabulary mirror; the canonical version is in
        /// SuperStatus.ApiService.PublicStatusApi.MapStateLabel (#108). Kept
        /// inline here so the Services project doesn't reverse-depend on
        /// ApiService. Wired up via a shared module in a later cleanup.
        /// </summary>
        public static string MostRecentState(StatusCheck check, HistoricalStatusData? mostRecent)
        {
            if (mostRecent is null) return "unknown";
            if (mostRecent.CheckFailed) return "down";
            if (mostRecent.HttpStatusCode != check.ExpectedStatusCode) return "down";
            if (mostRecent.ResponseTimeInMs > GetSlowThresholdMs(check)) return "degraded";
            return "up";
        }

        /// <summary>
        /// #293: THE slow-threshold read — the linked SLA's SlowThresholdMs.
        /// The startup backfill assigns every check an SLA (and fails startup
        /// otherwise), so a missing link here is an invariant violation, not a
        /// fallback case: throwing beats silently classifying with a threshold
        /// that no longer drives anything.
        /// </summary>
        public static long GetSlowThresholdMs(StatusCheck check)
            => check.Sla?.SlowThresholdMs
               ?? throw new InvalidOperationException(
                   $"Status check {check.Id} ('{check.Title}') has no linked SLA loaded — the #293 startup backfill guarantees one; load checks through IStatusCheckRepository so the Sla navigation is included.");

        /// <summary>
        /// Sort + nearest-rank percentile (Hermes #104 adjustment:
        /// PERCENTILE_CONT is Postgres-only; compute after fetching).
        /// </summary>
        public static int Percentile(IList<int> samples, double pct)
        {
            if (samples.Count == 0) return 0;
            var sorted = samples.OrderBy(x => x).ToArray();
            int rank = (int)Math.Ceiling(pct * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }

        /// <summary>
        /// Hero state rule. up if every service up *and* no incidents in
        /// window; down if any service down; degraded if any service degraded
        /// OR any open incident.
        /// </summary>
        public static string ComputeOverall(int up, int degraded, int down, int incidents)
        {
            if (down > 0) return "down";
            if (degraded > 0) return "degraded";
            if (incidents > 0) return "degraded";
            return "up";
        }

        public async Task<HistoricalStatusData> ExecuteStatusCheck(StatusCheck statusCheck)
        {

            // #86: time the whole attempt (StartNew), and stop after the
            // try/catch so the duration histogram captures failure/timeout
            // latency too — not just successful responses.
            var stopwatch = Stopwatch.StartNew();
            // Issue #77: pooled, instrumented, 10 s-timeout client from the
            // factory. NOT disposed — the factory owns the handler lifetime.
            var client = httpClientFactory.CreateClient(StatusCheckHttpClients.StatusCheck);
            bool checkFailed = false;

            int httpStatusCode;
            long responseTimeInMs;
            try
            {
                var response = await client.GetAsync(statusCheck.StatusCheckUrl);
                httpStatusCode = (int)response.StatusCode;
                responseTimeInMs = stopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                // #85: pass the exception object (preserves stack/context) and
                // keep the URL in a structured field rather than interpolating
                // ex.Message into the template. Stays at Information — an
                // unreachable target is an expected, high-frequency event for a
                // status monitor, not a system warning.
                logger.LogInformation(ex, "Failed to execute status check on {Url}", statusCheck.StatusCheckUrl);
                httpStatusCode = 0;
                responseTimeInMs = 0;
                checkFailed = true;
            }
            stopwatch.Stop();
            StatusCheckResult result = new StatusCheckResult(statusCheck, responseTimeInMs, httpStatusCode, checkFailed);
            HistoricalStatusData historicalStatusData = new HistoricalStatusData(result, CalculateFailTypeOfHistoricalStatusData(statusCheck, result));

            // #86: one counter increment + one duration sample per executed
            // check, tagged by fail_type. Recorded on every path (incl.
            // unreachable/timeout) so failures stay visible in metrics.
            var failTypeTag = StatusCheckMetrics.FailTypeTag(historicalStatusData.FailType);
            StatusCheckMetrics.ChecksExecuted.Add(1, failTypeTag);
            StatusCheckMetrics.CheckDuration.Record(stopwatch.Elapsed.TotalMilliseconds, failTypeTag);

            // Issue #75: post-tasks intentionally not invoked here. The job
            // calls RunPostStatusCheckTasks once after SaveStatusCheckResult so
            // the resulting action FK points at a persisted parent and the
            // throttle SELECT runs once per check per tick, not twice.
            return historicalStatusData;
        }
        public async Task<HistoricalStatusData> SaveStatusCheckResult(HistoricalStatusData historicalStatusData)
        {
            return await historicalStatusDataRepository.AddAndSave(historicalStatusData);
        }
        public async Task<HistoricalStatusAction> SaveStatusCheckAction(HistoricalStatusAction action)
        {
            return await historicalStatusActionRepository.AddAndSave(action);
        }
        public async Task<HistoricalStatusAction?> RunPostStatusCheckTasks(StatusCheck statusCheck, HistoricalStatusData historicalStatusData)
        {
            if (historicalStatusData.FailType == FailType.NoFail)
            {
                return null;
            }

            // #291: webhook dispatch resolves ONLY through the link table. The
            // legacy embedded fields no longer gate anything here — the startup
            // backfill / edit normalization translate them into links, so a
            // legacy-configured check behaves identically. No links (incl. a
            // fresh default-off check) → no-op.
            if (statusCheckLinkRepository is null)
            {
                return null;
            }
            List<StatusCheckWebhook> links = await statusCheckLinkRepository.GetWebhookLinksAsync(statusCheck.Id);
            if (links.Count == 0)
            {
                return null;
            }

            HistoricalStatusAction? action = null;
            foreach (var link in links)
            {
                Webhook webhook = link.Webhook!;

                if (!webhook.IsEnabled)
                {
                    // #291: a disabled target is skipped with explicit audit
                    // evidence (distinguishable from a throttle skip by the reason).
                    logger.LogInformation("NOT running webhook {Webhook} for {Title} because the target is disabled!", webhook.Name, statusCheck.Title);
                    await SaveLogAsync(SkippedLog(statusCheck, historicalStatusData, webhook, "target disabled"));
                    continue;
                }

                // #291: per-(check, webhook) throttle — anchor on the link, window
                // on the target. Each linked webhook throttles independently.
                if (link.LastFiredUtc is not null
                    && link.LastFiredUtc.Value.AddMinutes(webhook.ThrottleMinutes) > DateTime.UtcNow)
                {
                    logger.LogInformation("NOT running webhook {Webhook} for {Title} because of active throttle!", webhook.Name, statusCheck.Title);
                    // Issue #107: throttle-skipped rows are explicit audit
                    // evidence — "we would have fired but the throttle blocked
                    // it". HttpStatusCode + ResponseTimeMs are 0; the row is
                    // tagged WebhookOutcome.Skipped (distinct from Success) so
                    // the admin "failures only" / "real attempts" filters work
                    // cleanly.
                    await SaveLogAsync(SkippedLog(statusCheck, historicalStatusData, webhook, errorMessage: null));
                    continue;
                }

                bool success = await FireWebhookAsync(statusCheck, historicalStatusData, webhook);
                if (success)
                {
                    // The throttle anchor advances on Success only — a failed
                    // attempt is retried next tick, exactly like the legacy
                    // action-history anchor it replaces.
                    link.LastFiredUtc = DateTime.UtcNow;
                    await statusCheckLinkRepository.SaveChangesAsync();
                    // HistoricalStatusAction is 1:1 with the tick row, so
                    // multi-target dispatch still records at most one action per tick.
                    action ??= new HistoricalStatusAction(historicalStatusData, ActionType.Webhook, DateTime.UtcNow);
                }
            }
            return action;
        }

        private static WebhookExecutionLog SkippedLog(StatusCheck statusCheck, HistoricalStatusData historicalStatusData, Webhook webhook, string? errorMessage)
            => new()
            {
                StatusCheckId = statusCheck.Id,
                WebhookId = webhook.Id,
                HistoricalStatusDataId = historicalStatusData.Id == 0 ? null : historicalStatusData.Id,
                AttemptedUtc = DateTime.UtcNow,
                TargetUrl = webhook.Url,
                HttpStatusCode = 0,
                ResponseTimeMs = 0,
                Outcome = WebhookOutcome.Skipped,
                ErrorMessage = errorMessage,
            };

        /// <summary>One outbound attempt against a linked webhook target, with the
        /// per-outcome audit row + metric (#107 semantics unchanged; rows carry the
        /// target's WebhookId since #291). Returns true on a 2xx response.</summary>
        private async Task<bool> FireWebhookAsync(StatusCheck statusCheck, HistoricalStatusData historicalStatusData, Webhook webhook)
        {
            logger.LogInformation("Executing status check post tasks for {Title}...", statusCheck.Title);

            WebhookTestFireResult attempt = await AttemptWebhookAsync(webhook.Url);
            if (attempt.Outcome == WebhookOutcome.NonSuccess)
            {
                logger.LogInformation("Failed to execute web hook on error for {Title}", statusCheck.Title);
            }

            await SaveLogAsync(new WebhookExecutionLog
            {
                StatusCheckId = statusCheck.Id,
                WebhookId = webhook.Id,
                HistoricalStatusDataId = historicalStatusData.Id == 0 ? null : historicalStatusData.Id,
                AttemptedUtc = DateTime.UtcNow,
                TargetUrl = attempt.TargetUrl,
                HttpStatusCode = attempt.HttpStatusCode,
                ResponseTimeMs = attempt.ResponseTimeMs,
                Outcome = attempt.Outcome,
                ErrorMessage = attempt.ErrorMessage,
            });
            StatusCheckMetrics.WebhooksFired.Add(1, StatusCheckMetrics.OutcomeTag(attempt.Outcome));
            return attempt.Outcome == WebhookOutcome.Success;
        }

        /// <summary>
        /// #291 Phase B: operator test-fire — the same wire attempt real dispatch
        /// makes (same named client / timeout / outcome mapping), with no
        /// IsEnabled or throttle gate (a deliberate probe), no dispatch metric
        /// and NO audit row: WebhookExecutionLog.StatusCheckId is a required FK
        /// and a test has no triggering check (no schema change in this phase),
        /// so the caller surfaces the returned result inline instead.
        /// </summary>
        public async Task<WebhookTestFireResult> TestFireWebhookAsync(Webhook webhook, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Test-firing webhook {Webhook} ({Url})", webhook.Name, webhook.Url);
            return await AttemptWebhookAsync(webhook.Url);
        }

        /// <summary>
        /// The single wire-attempt core shared by real dispatch and the #291
        /// test-fire. Issue #77: uses the factory's named "status-webhook"
        /// client — pooled handler + the shared 10 s timeout configured at
        /// registration. A hung target surfaces as TaskCanceledException
        /// (mapped to WebhookOutcome.Timeout), bounding how long the scoped
        /// DbContext stays pinned. NOT disposed: the factory owns the handler
        /// lifetime.
        /// </summary>
        private async Task<WebhookTestFireResult> AttemptWebhookAsync(string url)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var client = httpClientFactory.CreateClient(StatusCheckHttpClients.Webhook);
                var response = await client.GetAsync(url);
                stopwatch.Stop();
                int statusCode = (int)response.StatusCode;
                return new WebhookTestFireResult
                {
                    Outcome = response.IsSuccessStatusCode ? WebhookOutcome.Success : WebhookOutcome.NonSuccess,
                    HttpStatusCode = statusCode,
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    ErrorMessage = response.IsSuccessStatusCode ? null : SanitizeErrorMessage($"HTTP {statusCode} {response.ReasonPhrase}"),
                    TargetUrl = url,
                };
            }
            catch (TaskCanceledException tce)
            {
                stopwatch.Stop();
                return new WebhookTestFireResult
                {
                    Outcome = WebhookOutcome.Timeout,
                    HttpStatusCode = 0,
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    ErrorMessage = SanitizeErrorMessage(tce.Message),
                    TargetUrl = url,
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new WebhookTestFireResult
                {
                    Outcome = WebhookOutcome.TransportFailure,
                    HttpStatusCode = 0,
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    ErrorMessage = SanitizeErrorMessage(ex.Message),
                    TargetUrl = url,
                };
            }
        }

        private async Task SaveLogAsync(WebhookExecutionLog log)
        {
            try
            {
                await webhookExecutionLogRepository.AddAndSave(log);
            }
            catch (Exception ex)
            {
                // The audit log is best-effort: if its own save fails we log
                // the failure and continue — never abort the calling status
                // check just because the audit table is unhappy.
                logger.LogError(ex, "Failed to persist WebhookExecutionLog for check {CheckId}", log.StatusCheckId);
            }
        }

        /// <summary>
        /// Truncate + scrub the captured message. Hermes review on #107:
        /// raw exception/response bodies must not be stored unbounded
        /// (PII / token risk + table bloat).
        /// </summary>
        private static string SanitizeErrorMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            // Drop newlines so the audit row stays single-line.
            string s = raw.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length > WebhookExecutionLog.MaxErrorMessageLength
                ? s[..WebhookExecutionLog.MaxErrorMessageLength] + "…"
                : s;
        }
        public async Task<HistoricalStatusDataViewModel> RunCheckNowAsync(long statusCheckId, CancellationToken cancellationToken = default)
        {
            // Issue #105. Single helper that mirrors the scheduler's exact
            // post-#75 pipeline. Endpoint resolves the StatusCheck first;
            // this method throws if it was deleted mid-call.
            StatusCheck check = await statusCheckRepository.GetStatusCheckById(statusCheckId)
                ?? throw new InvalidOperationException($"Status check {statusCheckId} not found.");

            logger.LogInformation("Manual run triggered for {Title} ({Id})", check.Title, check.Id);

            // Execute → Save → RunPostTasks (synchronous) → SaveAction —
            // every persistence step awaits before this method returns so a
            // caller observing the row also observes any side effects.
            HistoricalStatusData result = await ExecuteStatusCheck(check);
            HistoricalStatusData saved = await SaveStatusCheckResult(result);
            // #168: capture the down-since state BEFORE recording clears it, so a
            // manual run that recovers a down check resolves its linked auto-incident
            // (and a manual run during sustained downtime can still enqueue a draft) —
            // exactly as the scheduler tick does. Without this, a manual recovery
            // would clear DownSinceUtc and strand the public auto-incident open.
            bool wasDown = check.DownSinceUtc.HasValue;
            // #83: a manual run reflects the check's real health too — keep the
            // backoff counter consistent (reset on healthy, increment on fail).
            await RecordCheckOutcomeAsync(check, saved.FailType, cancellationToken);
            if (autoIncidentCoordinator is not null)
            {
                await autoIncidentCoordinator.EvaluateAsync(check, saved.FailType, wasDown, cancellationToken);
            }
            HistoricalStatusAction? action = await RunPostStatusCheckTasks(check, saved);
            if (action is not null)
            {
                await SaveStatusCheckAction(action);
            }
            return new HistoricalStatusDataViewModel(saved);
        }

        public async Task RecordCheckOutcomeAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default)
        {
            int updatedFailures = failType == FailType.NoFail ? 0 : check.ConsecutiveFailures + 1;
            // #168: DownSinceUtc tracks the healthy→failing edge — stamped on the
            // first failure, preserved while failing, cleared on recovery. It is the
            // precise "down since" the sustained-downtime threshold measures against
            // (robust to #83 widening the polling interval).
            DateTime? updatedDownSince = failType == FailType.NoFail
                ? null
                : (check.DownSinceUtc ?? DateTime.UtcNow);

            if (updatedFailures == check.ConsecutiveFailures && updatedDownSince == check.DownSinceUtc)
            {
                return; // steady-healthy (or already 0) — no write.
            }
            check.ConsecutiveFailures = updatedFailures;
            check.DownSinceUtc = updatedDownSince;
            await statusCheckRepository.UpdateAndSave(check);
        }

        public async Task<StatusCheck> SetEnabledAsync(long statusCheckId, bool enabled, CancellationToken cancellationToken = default)
        {
            StatusCheck check = await statusCheckRepository.GetStatusCheckById(statusCheckId)
                ?? throw new InvalidOperationException($"Status check {statusCheckId} not found.");
            if (check.Enabled == enabled)
            {
                return check;
            }
            check.Enabled = enabled;
            logger.LogInformation("Operator {Action} check {Title} ({Id})", enabled ? "ENABLED" : "PAUSED", check.Title, check.Id);
            return await statusCheckRepository.UpdateAndSave(check);
        }

        // Issue #164. Hard delete; the DB's ON DELETE CASCADE FKs remove the
        // check's historical ticks, daily rollups and webhook logs. Incidents
        // are global (no check FK) and are untouched.
        public async Task<bool> DeleteStatusCheckAsync(long statusCheckId, CancellationToken cancellationToken = default)
        {
            var existing = await statusCheckRepository.GetStatusCheckById(statusCheckId);
            if (existing is null) return false;

            logger.LogInformation("Operator DELETED check {Title} ({Id})", existing.Title, existing.Id);
            await statusCheckRepository.DeleteAndSave(existing, cancellationToken);
            return true;
        }

        public async Task<StatusCheck> AddOrUpdateStatusCheck(StatusCheckViewModelBase statusCheck)
        {
            if (statusCheck.Id > 0)
            {
                var existingStatusCheck = await statusCheckRepository.GetStatusCheckById(statusCheck.Id) ?? throw new Exception($"Failed to find status check with id {statusCheck.Id}");

                existingStatusCheck.Title = statusCheck.Title;
                existingStatusCheck.StatusCheckUrl = statusCheck.StatusCheckUrl;
                existingStatusCheck.ExpectedStatusCode = statusCheck.ExpectedStatusCode;
                existingStatusCheck.Description = statusCheck.Description;
                existingStatusCheck.Enabled = statusCheck.Enabled;
                existingStatusCheck.ServiceLogoUrl = statusCheck.ServiceLogoUrl;
                // #82: clamp server-side (5–3600) — the form min/max is advisory.
                existingStatusCheck.IntervalSeconds = StatusCheckSchedule.Clamp(statusCheck.IntervalSeconds);
                // #168: per-check AI auto-incident opt-in.
                existingStatusCheck.AutoIncidentEnabled = statusCheck.AutoIncidentEnabled;
                // #253: per-check alert rules (counts/minutes floored at 0; the
                // server-managed dedup/throttle bookkeeping is never overwritten here).
                existingStatusCheck.AlertOnFailureThreshold = Math.Max(0, statusCheck.AlertOnFailureThreshold);
                existingStatusCheck.AlertOnOutageMinutes = Math.Max(0, statusCheck.AlertOnOutageMinutes);
                existingStatusCheck.AlertOnRecovery = statusCheck.AlertOnRecovery;
                existingStatusCheck.AlertThrottleMinutes = Math.Max(0, statusCheck.AlertThrottleMinutes);

                return await statusCheckRepository.UpdateAndSave(existingStatusCheck);

            }

            var newStatusCheck = new StatusCheck
            {
                Title = statusCheck.Title,
                StatusCheckUrl = statusCheck.StatusCheckUrl,
                ExpectedStatusCode = statusCheck.ExpectedStatusCode,
                Description = statusCheck.Description,
                Enabled = statusCheck.Enabled,
                ServiceLogoUrl = statusCheck.ServiceLogoUrl,
                // #82: clamp server-side (5–3600).
                IntervalSeconds = StatusCheckSchedule.Clamp(statusCheck.IntervalSeconds),
                // #168: persist the per-check AI auto-incident opt-in on create too
                // (not only on update) so enabling it while adding a check sticks.
                AutoIncidentEnabled = statusCheck.AutoIncidentEnabled,
                // #253: per-check alert rules on create too (counts/minutes floored at 0).
                AlertOnFailureThreshold = Math.Max(0, statusCheck.AlertOnFailureThreshold),
                AlertOnOutageMinutes = Math.Max(0, statusCheck.AlertOnOutageMinutes),
                AlertOnRecovery = statusCheck.AlertOnRecovery,
                AlertThrottleMinutes = Math.Max(0, statusCheck.AlertThrottleMinutes),
                // Creation timestamp, set once at add time.
                Created = DateTime.UtcNow,
            };
            return await statusCheckRepository.AddAndSave(newStatusCheck);

        }
        private FailType CalculateFailTypeOfHistoricalStatusData(StatusCheck statusCheck, StatusCheckResult statusCheckResult)
        {
            if (statusCheckResult.CheckFailed)
            {
                return FailType.Unreachable;
            }
            else if (statusCheckResult.HttpStatusCode != statusCheck.ExpectedStatusCode)
            {
                return FailType.StatusCode;
            }
            else if (statusCheckResult.ResponseTimeInMs > GetSlowThresholdMs(statusCheck))
            {
                // #293: collection-time slow marking reads the linked SLA.
                return FailType.ResponseTime;
            }
            else
            {
                return FailType.NoFail;
            }
        }

    }
}
