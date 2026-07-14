using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Data.Extensions;
using SuperStatus.Services.Http;
using SuperStatus.Services.Providers;
using SuperStatus.Services.Providers.Http;
using SuperStatus.Services.Scheduling;
using SuperStatus.Services.Telemetry;
using System.Diagnostics;
using SuperStatus.Services.Plugins;

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

        /// <summary>
        /// Epic #271 / #317 Phase 2a. A check's recent metric series — the provider's
        /// declared <c>MetricDefs</c> + the per-tick values parsed from recent ticks'
        /// <c>MetricsJson</c> (raw-tick window only; no rollup). Null when the check is
        /// unknown. Backs the Phase-2c dashboard rendering.
        /// </summary>
        Task<CheckMetricsViewModel?> GetRecentMetricsAsync(long statusCheckId, int count, CancellationToken cancellationToken = default);

        /// <summary>#320 Phase 2b: record an inbound heartbeat ping for the check whose
        /// token matches (stamps LastHeartbeatUtc = now). Returns true when a row was
        /// updated, false for an unknown/rotated token — the only signal the anonymous
        /// ping endpoint surfaces (204 vs 404).</summary>
        Task<bool> RecordHeartbeatAsync(string token, CancellationToken cancellationToken = default);

        /// <summary>#320 Phase 2b: the heartbeat token for an operator's check (so the
        /// edit dialog can render the ping URL). Returns null when the check is unknown or
        /// not a heartbeat check. Operator-authenticated path only — the token is a
        /// credential and never rides the anonymous read VM.</summary>
        Task<string?> GetHeartbeatTokenAsync(long statusCheckId, CancellationToken cancellationToken = default);

        /// <summary>#320 Phase 2b: rotate a heartbeat check's token, returning the new one.
        /// The old URL stops working immediately (the token is the lookup key). Returns
        /// null when the check is unknown or not a heartbeat check.</summary>
        Task<string?> RegenerateHeartbeatTokenAsync(long statusCheckId, CancellationToken cancellationToken = default);

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

        /// <summary>
        /// Epic #271 / #312 Phase 1. THE single gate the scheduled tick and the manual
        /// run-now path both consult before probing: resolves the check's
        /// <c>ProviderType</c> to a registered provider and validates its <c>ConfigJson</c>
        /// against that provider's versioned schema. Returns either a runnable
        /// (provider + validated config) pair or a calm disable reason. Unknown / missing /
        /// invalid config disables the check identically everywhere — never a crash,
        /// never a silent default probe.
        /// </summary>
        ProbeResolution ResolveProbe(StatusCheck statusCheck);

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
    public class StatusCheckService(IStatusCheckRepository statusCheckRepository, IHistoricalStatusDataRepository historicalStatusDataRepository, IHistoricalStatusActionRepository historicalStatusActionRepository, IWebhookExecutionLogRepository webhookExecutionLogRepository, IDailyStatusRollupRepository dailyStatusRollupRepository, IHttpClientFactory httpClientFactory, ILogger<StatusCheckService> logger, IAutoIncidentCoordinator? autoIncidentCoordinator = null, IStatusCheckLinkRepository? statusCheckLinkRepository = null, ICheckProviderRegistry? checkProviderRegistry = null, SuperStatus.Services.Alerts.IAlertEvaluator? alertEvaluator = null) : IStatusCheckService
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

            // #312: surface provider-config validity so the console can render the calm
            // "check disabled — fix config" state. Uses the same ResolveProbe gate the
            // scheduler and run-now consult, so the surfaced reason matches exactly.
            if (result.Results.Count > 0)
            {
                var entitiesById = statusCheckSet.Results.ToDictionary(e => e.Id);
                foreach (var vm in result.Results)
                {
                    if (!entitiesById.TryGetValue(vm.Id, out var entity)) continue;
                    var resolution = ResolveProbe(entity);
                    vm.ConfigValid = !resolution.IsDisabled;
                    vm.ConfigError = resolution.DisableReason;
                    // #392: rebuild the edit dialog's read-side config for non-http providers
                    // from the stored ConfigJson (the data layer only seeds http from its
                    // legacy columns), so a saved AI check reopens with its values, not blank.
                    PopulateReadProviderConfig(vm, entity);
                }
            }

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

        public async Task<CheckMetricsViewModel?> GetRecentMetricsAsync(long statusCheckId, int count, CancellationToken cancellationToken = default)
        {
            StatusCheck? check = await GetStatusCheck(statusCheckId);
            if (check is null) return null;

            string providerType = string.IsNullOrWhiteSpace(check.ProviderType) ? HttpCheckProvider.TypeId : check.ProviderType.Trim();
            var defs = checkProviderRegistry?.Find(providerType)?.Descriptor.MetricDefs ?? Array.Empty<MetricDef>();

            var ticks = await GetRecentTicks(statusCheckId, count);
            var samples = new List<MetricSampleViewModel>();
            foreach (var t in ticks.OrderBy(t => t.TimeOfCheckUTC))
            {
                var values = ParseMetricValues(t.MetricsJson);
                if (values.Count > 0)
                    samples.Add(new MetricSampleViewModel { TimeUtc = t.TimeOfCheckUTC, Values = values });
            }

            return new CheckMetricsViewModel
            {
                StatusCheckId = statusCheckId,
                ProviderType = providerType,
                MetricDefs = defs.Select(d => new ProviderMetricDefViewModel
                {
                    Key = d.Key,
                    Label = d.Label,
                    Unit = d.Unit,
                    Kind = d.Kind.ToString().ToLowerInvariant(),
                    WarnThreshold = d.WarnThreshold,
                    CritThreshold = d.CritThreshold,
                }).ToList(),
                Samples = samples,
            };
        }

        public Task<bool> RecordHeartbeatAsync(string token, CancellationToken cancellationToken = default)
        {
            // Pure delegation to the narrow indexed UPDATE — the engine owns "now", the
            // repo owns the single set-based write. The token is never logged here.
            return statusCheckRepository.RecordHeartbeatAsync(token, DateTime.UtcNow, cancellationToken);
        }

        public async Task<string?> GetHeartbeatTokenAsync(long statusCheckId, CancellationToken cancellationToken = default)
        {
            StatusCheck? check = await GetStatusCheck(statusCheckId);
            if (check is null || !IsHeartbeat(check)) return null;
            return check.HeartbeatToken;
        }

        public async Task<string?> RegenerateHeartbeatTokenAsync(long statusCheckId, CancellationToken cancellationToken = default)
        {
            StatusCheck? check = await GetStatusCheck(statusCheckId);
            if (check is null || !IsHeartbeat(check)) return null;

            // The token IS the lookup key, so swapping it invalidates the old ping URL
            // the instant this save commits — no separate revocation needed.
            check.HeartbeatToken = Providers.Heartbeat.HeartbeatToken.Generate();
            await statusCheckRepository.UpdateAndSave(check, cancellationToken);
            return check.HeartbeatToken;
        }

        private static bool IsHeartbeat(StatusCheck check) =>
            string.Equals(check.ProviderType?.Trim(), Providers.Heartbeat.HeartbeatCheckProvider.TypeId, StringComparison.Ordinal);

        private static Dictionary<string, double> ParseMetricValues(string? metricsJson)
        {
            var values = new Dictionary<string, double>();
            if (string.IsNullOrWhiteSpace(metricsJson)) return values;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(metricsJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Number && p.Value.TryGetDouble(out var v))
                            values[p.Name] = v;
                }
            }
            catch (System.Text.Json.JsonException) { }
            return values;
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
            // #317: derive from the stored FailType the provider already classified — not
            // from the HTTP-specific fields. Identical to the pre-#317 recompute for HTTP
            // (the stored FailType was computed the same way), but correct for non-HTTP
            // providers (e.g. an AI check whose HttpStatusCode is always 0).
            return mostRecent.FailType switch
            {
                FailType.NoFail => "up",
                FailType.ResponseTime => "degraded",
                _ => "down", // StatusCode, Unreachable
            };
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

        // Epic #271 / #312 Phase 1. THE shared resolve-or-disable gate. The HTTP
        // fallback keeps ad-hoc / pre-migration StatusCheck objects (which carry no
        // ConfigJson) probing exactly as before, by synthesizing their config from the
        // legacy columns. A null registry (e.g. a unit test constructing the service
        // directly) still resolves the built-in HTTP provider via the same factory.
        public ProbeResolution ResolveProbe(StatusCheck statusCheck)
        {
            string providerType = string.IsNullOrWhiteSpace(statusCheck.ProviderType)
                ? HttpCheckProvider.TypeId
                : statusCheck.ProviderType.Trim();

            ICheckProvider? provider = checkProviderRegistry?.Find(providerType) ?? FallbackProvider(providerType);
            if (provider is null)
            {
                return ProbeResolution.Disabled($"unknown provider type '{providerType}'");
            }

            string? configJson = statusCheck.ConfigJson;
            if (string.IsNullOrWhiteSpace(configJson) && provider.Descriptor.TypeId == HttpCheckProvider.TypeId)
            {
                // Phase-1 bridge: a row not yet carrying ConfigJson gets it from the
                // legacy HTTP columns so behavior is identical to pre-#312.
                configJson = HttpCheckConfig.ToJson(statusCheck.StatusCheckUrl, statusCheck.ExpectedStatusCode, HttpCheckProvider.SchemaVersion);
            }

            string? error = provider.Descriptor.ConfigSchema.Validate(configJson);
            if (error is not null)
            {
                return ProbeResolution.Disabled(error);
            }

            return ProbeResolution.Ok(provider, configJson ?? string.Empty);
        }

        // Only the built-in HTTP provider has a no-DI fallback (it needs just the
        // factory this service already holds). Any other type with no registry entry
        // is genuinely unknown → disabled.
        private ICheckProvider? FallbackProvider(string providerType)
            => providerType == HttpCheckProvider.TypeId ? new HttpCheckProvider(httpClientFactory) : null;

        public async Task<HistoricalStatusData> ExecuteStatusCheck(StatusCheck statusCheck)
        {
            // #312: probe through the resolved provider. The scheduled tick and run-now
            // both gate on ResolveProbe first, so a disabled check never reaches here;
            // the defensive branch contains it as Unreachable rather than crash.
            ProbeResolution resolution = ResolveProbe(statusCheck);

            // #86: time the whole attempt and stop after, so the duration histogram
            // captures failure/timeout latency too — not just successful responses.
            var stopwatch = Stopwatch.StartNew();
            ProbeResult probe;
            if (resolution.IsDisabled)
            {
                logger.LogWarning("ExecuteStatusCheck reached a check with unusable config ({Reason}) for {Title}; recording Unreachable", resolution.DisableReason, statusCheck.Title);
                probe = ProbeResult.Unreachable(resolution.DisableReason);
            }
            else
            {
                probe = await RunProbeSafelyAsync(resolution.Provider!, statusCheck, resolution.EffectiveConfigJson);
            }
            stopwatch.Stop();

            // Apply the cross-cutting latency SLO (the linked SLA's slow threshold): a
            // healthy result that exceeds it becomes ResponseTime/degraded — the exact
            // pre-#312 rule, kept in the core so it stays SLA-driven, not HTTP-specific.
            FailType failType = ApplyLatencySlo(statusCheck, probe);

            StatusCheckResult result = new StatusCheckResult(statusCheck, probe.LatencyMs, probe.HttpStatusCode, !probe.Reachable);
            // #317: a provider may only persist metrics it DECLARED — sanitize the
            // emitted MetricsJson against the provider's MetricDefs (drops undeclared /
            // non-numeric keys; null when nothing valid, so HTTP checks stay null as in
            // Phase 1).
            string? metricsJson = resolution.Provider is { } metricsProvider
                ? MetricsValidator.Sanitize(probe.MetricsJson, metricsProvider.Descriptor.MetricDefs)
                : null;
            HistoricalStatusData historicalStatusData = new HistoricalStatusData(result, failType)
            {
                MetricsJson = metricsJson,
            };

            // #86: one counter increment + one duration sample per executed check,
            // tagged by fail_type. Recorded on every path (incl. unreachable/timeout)
            // so failures stay visible in metrics.
            var failTypeTag = StatusCheckMetrics.FailTypeTag(historicalStatusData.FailType);
            StatusCheckMetrics.ChecksExecuted.Add(1, failTypeTag);
            StatusCheckMetrics.CheckDuration.Record(stopwatch.Elapsed.TotalMilliseconds, failTypeTag);

            // Issue #75: post-tasks intentionally not invoked here. The job calls
            // RunPostStatusCheckTasks once after SaveStatusCheckResult.
            return historicalStatusData;
        }

        // #312: optional hard-backstop OVERRIDE. Normally the backstop is derived from
        // the provider's own ProbeTimeout (below); a test can set this to drive it short.
        public TimeSpan? ProbeBackstop { get; set; }

        // #312/#317: hard probe containment. The per-probe timeout is the provider's
        // declared ProbeTimeout (HTTP 10s; the AI canary longer); a WaitAsync backstop
        // beyond it plus a try/catch convert a provider that throws, hangs, OR ignores
        // cancellation into a normalized Unreachable result — it can never propagate into
        // the scheduler tick, backoff, auto-incident, or alerts.
        private async Task<ProbeResult> RunProbeSafelyAsync(ICheckProvider provider, StatusCheck check, string configJson)
        {
            var timeout = provider.Descriptor.ProbeTimeout;
            // #320: pass the last inbound signal (heartbeat) so a push provider can
            // classify freshness without reading app state. Null for pull providers.
            var context = new ProbeContext(check.Id, check.Title, configJson, timeout, check.LastHeartbeatUtc);

            using var cts = new CancellationTokenSource(ProbeBackstop ?? (timeout + TimeSpan.FromSeconds(5)));
            try
            {
                // WaitAsync abandons the await if the backstop fires — so a provider that
                // never observes cts.Token is contained, not awaited forever.
                return await provider.ProbeAsync(context, cts.Token).WaitAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Provider '{Provider}' threw or exceeded the probe backstop probing {Title}; recording Unreachable", provider.Descriptor.TypeId, check.Title);
                return ProbeResult.Unreachable(ex.Message);
            }
        }

        // #293/#312: the latency SLO. Identical to the pre-#312 third classification
        // branch — a healthy (NoFail) result slower than the linked SLA's slow threshold
        // is ResponseTime. GetSlowThresholdMs throws if no SLA is loaded (an invariant
        // the startup backfill guarantees), exactly as before.
        private static FailType ApplyLatencySlo(StatusCheck check, ProbeResult probe)
            => probe.FailType == FailType.NoFail && probe.LatencyMs > GetSlowThresholdMs(check)
                ? FailType.ResponseTime
                : probe.FailType;
        public async Task<HistoricalStatusData> SaveStatusCheckResult(HistoricalStatusData historicalStatusData)
        {
            return await historicalStatusDataRepository.AddAndSave(historicalStatusData);
        }
        public async Task<HistoricalStatusAction> SaveStatusCheckAction(HistoricalStatusAction action)
        {
            return await historicalStatusActionRepository.AddAndSave(action);
        }
        public Task<HistoricalStatusAction?> RunPostStatusCheckTasks(StatusCheck statusCheck, HistoricalStatusData historicalStatusData)
        {
            // #343 Phase 4: webhooks are folded into the notification-channel model — they
            // now fire through AlertEvaluator (per-profile webhook channels, a POST JSON
            // payload, unified AlertDeliveryLog), NOT from here. This post-task is therefore
            // inert; the scheduler still calls it once per tick as a seam, but it records no
            // HistoricalStatusAction(Webhook). The standalone Webhook target surface + its
            // tables are retained (deprecated) for the migration + history; a later step
            // removes them.
            return Task.FromResult<HistoricalStatusAction?>(null);
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
            // #343 Phase 4 (Hermes): a manual "Run now" must dispatch alerts through the
            // same evaluator the scheduler uses (email / web-push / the folded webhook
            // channel → AlertDeliveryLog) — matching the scheduler's order (after the
            // outcome + auto-incident steps, before the now-inert post-tasks). Without
            // this, manually-triggered checks would silently skip alert delivery.
            if (alertEvaluator is not null)
            {
                await alertEvaluator.EvaluateAsync(check, saved.FailType, cancellationToken);
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

            // #415: the check's linked incidents are removed atomically by the DB via the
            // Incident.SourceStatusCheckId FK (ON DELETE CASCADE) — so no app-level
            // cleanup here, and no phantom open incident can outlive its check.
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
                // #312: provider type + ConfigJson, kept in sync with the legacy
                // StatusCheckUrl / ExpectedStatusCode columns for the http provider.
                ApplyProviderConfig(existingStatusCheck, statusCheck);
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
            // #312: provider type + ConfigJson (synced with the http columns above).
            ApplyProviderConfig(newStatusCheck, statusCheck);
            return await statusCheckRepository.AddAndSave(newStatusCheck);

        }

        // #312: persist the provider type + config alongside the common fields. For the
        // only Phase-1 provider (http), ConfigJson mirrors the legacy StatusCheckUrl /
        // ExpectedStatusCode columns — which stay authoritative for old read consumers —
        // so behavior is unchanged (nothing reads ConfigJson for http except the
        // provider, and it produces the identical probe). The schema-driven dialog posts
        // values via the generic ProviderConfig map; an older client posting only the
        // legacy fields still works (the fallback below).
        // #392: read counterpart to ApplyProviderConfig. BuildProviderConfigForRead (data
        // layer) can only seed http's config from its legacy columns — it has no provider
        // schema, so it can't tell which fields are secret and leaves every non-http config
        // empty. That made a saved AI check reopen blank (and re-saving the blank form gutted
        // the stored config). Here — where the registry (hence each field's Kind) is available
        // — reconstruct the read-side ProviderConfig from the stored ConfigJson: non-secret
        // values are surfaced as strings (numbers/bools included) so the dialog pre-fills them,
        // while Secret fields are NEVER echoed (write-only "leave blank to keep"; a blank secret
        // on save preserves the stored value via ProviderConfigWriter).
        private void PopulateReadProviderConfig(StatusCheckViewModelBase vm, StatusCheck entity)
        {
            if (string.Equals(vm.ProviderType, HttpCheckProvider.TypeId, StringComparison.Ordinal)) return;
            if (string.IsNullOrWhiteSpace(entity.ConfigJson)) return;
            var provider = checkProviderRegistry?.Find(vm.ProviderType);
            if (provider is null) return; // unknown type: no schema — the disabled banner explains

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(entity.ConfigJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
                foreach (var field in provider.Descriptor.ConfigSchema.Fields)
                {
                    if (field.Kind == ConfigFieldKind.Secret) continue; // never echo secrets
                    if (!doc.RootElement.TryGetProperty(field.Key, out var el)) continue;
                    string? value = el.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => el.GetString(),
                        System.Text.Json.JsonValueKind.Number => el.GetRawText(),
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        _ => null,
                    };
                    if (value is not null) vm.ProviderConfig[field.Key] = value;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed stored config: leave the read map empty; ResolveProbe already
                // surfaces the check as disabled-with-reason.
            }
        }

        private void ApplyProviderConfig(StatusCheck entity, StatusCheckViewModelBase vm)
        {
            string providerType = string.IsNullOrWhiteSpace(vm.ProviderType)
                ? HttpCheckProvider.TypeId
                : vm.ProviderType.Trim();
            entity.ProviderType = providerType;

            if (providerType == HttpCheckProvider.TypeId)
            {
                string url = vm.ProviderConfig.TryGetValue(HttpCheckConfig.UrlKey, out var u) && !string.IsNullOrWhiteSpace(u)
                    ? u
                    : vm.StatusCheckUrl;
                int expected = vm.ProviderConfig.TryGetValue(HttpCheckConfig.ExpectedStatusCodeKey, out var e) && int.TryParse(e, out var parsed)
                    ? parsed
                    : vm.ExpectedStatusCode;

                entity.StatusCheckUrl = url;
                entity.ExpectedStatusCode = expected;
                entity.ConfigJson = HttpCheckConfig.ToJson(url, expected, HttpCheckProvider.SchemaVersion);
            }
            else
            {
                // Future providers: ConfigJson is the source of truth (the generic map).
                // ProviderConfigWriter stamps the schema version and applies the secret
                // rule (blank secret preserves the stored value). An unknown provider
                // type has no schema — store the values verbatim; ResolveProbe will
                // disable the check calmly on load.
                var provider = checkProviderRegistry?.Find(providerType);
                if (provider is not null)
                {
                    entity.ConfigJson = ProviderConfigWriter.Build(provider.Descriptor.ConfigSchema, vm.ProviderConfig, entity.ConfigJson);
                }
                else
                {
                    var obj = new System.Text.Json.Nodes.JsonObject { [ConfigSchema.VersionKey] = 1 };
                    foreach (var kv in vm.ProviderConfig) obj[kv.Key] = kv.Value;
                    entity.ConfigJson = obj.ToJsonString();
                }
            }

            // #320: heartbeat token lifecycle.
            if (providerType == Providers.Heartbeat.HeartbeatCheckProvider.TypeId)
            {
                // A fresh (or just-converted-to-heartbeat) check mints an unguessable token
                // and gets grace from creation (LastHeartbeatUtc = now) so it can't
                // false-alarm before the first ping. An existing token is preserved
                // (regenerate is a separate, explicit action).
                if (string.IsNullOrEmpty(entity.HeartbeatToken))
                {
                    entity.HeartbeatToken = Providers.Heartbeat.HeartbeatToken.Generate();
                    entity.LastHeartbeatUtc = DateTime.UtcNow;
                }
            }
            else if (entity.HeartbeatToken is not null)
            {
                // Converting AWAY from heartbeat must REVOKE the ping credential — otherwise
                // the old anonymous /heartbeat/{token} URL would keep recording against a
                // now-non-heartbeat row. Clearing the token drops the row out of the
                // partial-unique index so the old URL 404s immediately; converting back to
                // heartbeat later mints a brand-new token above (the old URL stays dead).
                entity.HeartbeatToken = null;
                entity.LastHeartbeatUtc = null;
            }
        }
    }
}
