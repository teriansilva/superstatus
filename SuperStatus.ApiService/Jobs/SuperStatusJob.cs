using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.Entities;
using SuperStatus.Services.Scheduling;
using SuperStatus.Services.Services;

namespace SuperStatus.Scheduler
{
    /// <summary>One status-check tick: list the due checks and fan out over
    /// them. Issue #84 replaced the Quartz IJob with this plain orchestrator
    /// driven by <see cref="StatusCheckSchedulerService"/>; the across-tick
    /// non-overlap guard that Quartz's [DisallowConcurrentExecution] used to
    /// provide is now structural — the hosted service awaits one tick fully
    /// before starting the next.</summary>
    public interface IStatusCheckTick
    {
        Task RunDueChecksAsync(CancellationToken cancellationToken = default);
    }

    // Within-tick fan-out (issue #78) is bounded by MaxConcurrentChecks, and
    // each check runs in its own DI scope so no scoped SuperStatusDb (one
    // Npgsql connection) is shared across parallel work. Bounded fan-out ×
    // bounded pool is what keeps us off the "53300: sorry, too many clients
    // already" exhaustion (issue #71). Across-tick overlap is prevented by the
    // scheduler service awaiting each tick before the next (issue #84).
    public class SuperStatusCheckJob(
        IServiceScopeFactory scopeFactory,
        SchedulerConcurrencyOptions concurrency,
        ILogger<StatusCheckService> logger) : IStatusCheckTick
    {
        public async Task RunDueChecksAsync(CancellationToken ct = default)
        {

            // Snapshot only the due-check IDs ONCE, in its own short-lived scope.
            // We deliberately do NOT carry the StatusCheck entities across scope
            // boundaries: each worker below re-queries its check inside its own
            // scope so the whole Execute→Save pipeline operates on entities
            // tracked by that scope's DbContext. Passing a detached StatusCheck
            // into a different scope makes EF treat it as new on AddAsync and
            // the historical row never persists (Hermes review on #78).
            // Disabled checks are filtered here (issue #105) — manual /run-now
            // still bypasses the gate. Issue #82: among the enabled checks, only
            // those whose per-check IntervalSeconds has elapsed since their last
            // recorded tick are due this run. "Last tick" comes from persisted
            // history (durable across restarts / multi-instance), not an
            // in-memory map; a check with no history is due (first run).
            DateTime nowUtc = DateTime.UtcNow;
            List<long> dueCheckIds;
            int disabledCount;
            int notDueCount;
            await using (var listScope = scopeFactory.CreateAsyncScope())
            {
                var listService = listScope.ServiceProvider.GetRequiredService<IStatusCheckService>();
                var all = (await listService.GetStatusCheckSet()).Results;
                var enabled = all.Where(c => c.Enabled).ToList();
                disabledCount = all.Count - enabled.Count;

                var lastTimes = await listService.GetLastCheckTimesAsync(enabled.Select(c => c.Id).ToList(), ct);
                // #83: a failing check backs off — its effective interval widens
                // with ConsecutiveFailures (capped) so we stop hammering a down
                // endpoint. A healthy/recovered check uses its base interval.
                dueCheckIds = enabled
                    .Where(c => StatusCheckSchedule.IsDue(
                        lastTimes.TryGetValue(c.Id, out var last) ? last : (DateTime?)null,
                        StatusCheckSchedule.EffectiveIntervalSeconds(c.IntervalSeconds, c.ConsecutiveFailures),
                        nowUtc))
                    .Select(c => c.Id)
                    .ToList();
                notDueCount = enabled.Count - dueCheckIds.Count;
            }
            logger.LogInformation("Executing {DueCount} due checks ({DisabledCount} disabled, {NotDueCount} not yet due)...", dueCheckIds.Count, disabledCount, notDueCount);

            // Bounded fan-out. Clamp to ≥1 so a misconfigured 0/negative value
            // can never throw out of ParallelOptions or silently stall the tick.
            int degree = Math.Max(1, concurrency.MaxConcurrentChecks);

            await Parallel.ForEachAsync(
                dueCheckIds,
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                async (checkId, token) =>
                {
                    // One DI scope per check → one DbContext per check. Never
                    // shared across the parallel iterations (EF change tracker
                    // is not thread-safe).
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var statusCheckService = scope.ServiceProvider.GetRequiredService<IStatusCheckService>();
                    try
                    {
                        // Re-query inside this scope so the StatusCheck and the
                        // HistoricalStatusData it produces are tracked together.
                        StatusCheck? statusCheck = await statusCheckService.GetStatusCheck(checkId);
                        if (statusCheck is null)
                        {
                            // Deleted between the listing snapshot and now — skip.
                            return;
                        }

                        // #85: per-check progress logs fire for every check on
                        // every tick — Debug so production logs aren't flooded.
                        logger.LogDebug("Executing status check for {Title}...", statusCheck.Title);
                        HistoricalStatusData statusCheckResult = await statusCheckService.ExecuteStatusCheck(statusCheck);
                        logger.LogDebug("Completed status check for {Title} with response time {ResponseTimeMs} and StatusCode {StatusCode}!", statusCheck.Title, statusCheckResult.ResponseTimeInMs, statusCheckResult.HttpStatusCode);

                        statusCheckResult = await statusCheckService.SaveStatusCheckResult(statusCheckResult);
                        logger.LogDebug("Saved status check result for {Title}!", statusCheck.Title);
                        // #168: capture the down-since state BEFORE recording so the
                        // coordinator can act on the down→healthy edge (recovery).
                        bool wasDown = statusCheck.DownSinceUtc.HasValue;
                        // #83: fold the outcome into the backoff counter (reset
                        // on healthy, increment on failure) in this same scope,
                        // right after the result is saved, so the next tick's
                        // due calculation sees the updated count. #168: this also
                        // maintains DownSinceUtc.
                        await statusCheckService.RecordCheckOutcomeAsync(statusCheck, statusCheckResult.FailType, token);
                        // #168: enqueue an AI incident draft on sustained downtime, or
                        // resolve the linked auto-incident on recovery. Off the hot path
                        // (drafting runs in the background worker); inert unless AI is
                        // enabled + the check opted in.
                        var autoIncident = scope.ServiceProvider.GetRequiredService<IAutoIncidentCoordinator>();
                        await autoIncident.EvaluateAsync(statusCheck, statusCheckResult.FailType, wasDown, token);
                        // Issue #75: post-tasks invoked exactly once per check
                        // per tick, after save so the action's parent FK is
                        // populated. The exactly-once contract is unchanged by
                        // the fan-out — each check is independent.
                        HistoricalStatusAction? action = await statusCheckService.RunPostStatusCheckTasks(statusCheck, statusCheckResult);
                        if (action != null)
                        {
                            await statusCheckService.SaveStatusCheckAction(action);
                        }
                    }
                    catch (Exception ex)
                    {
                        // One failing check must not abort its siblings
                        // (issue #76). The exception is swallowed per-iteration
                        // so Parallel.ForEachAsync does not cancel the rest.
                        // Logged by id — the re-queried entity may not be in
                        // scope if the failure happened during the re-query.
                        logger.LogError(ex, "Status check failed for check {StatusCheckId}", checkId);
                    }
                });
        }
    }
}
