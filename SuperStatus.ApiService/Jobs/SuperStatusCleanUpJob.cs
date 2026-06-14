using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Configuration;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;

namespace SuperStatus.Scheduler
{
    /// <summary>One retention-cleanup tick. Issue #84 replaced the Quartz IJob
    /// with this plain orchestrator driven by <see cref="DbCleanupSchedulerService"/>;
    /// non-overlap (one cleanup at a time) is structural — the hosted service
    /// awaits each run before scheduling the next.</summary>
    public interface IDbCleanupTick
    {
        Task RunCleanupAsync(CancellationToken cancellationToken = default);
    }

    // Registered as a SINGLETON (the hosted service is a singleton), so it must
    // NOT constructor-inject the scoped repositories / SuperStatusDb — that
    // would be a captive dependency (Hermes review on #84). Like the
    // status-check tick, it takes IServiceScopeFactory and opens a fresh scope
    // per run, resolving the scoped repositories inside it.
    public class SuperStatusCleanUpJob(IServiceScopeFactory scopeFactory, ILogger<StatusCheckService> logger) : IDbCleanupTick
    {
        /// <summary>
        /// Issue #138. The rollup-computation version this build produces. The
        /// cleanup job does a one-time FULL re-backfill whenever the persisted
        /// marker is below this — fresh installs (marker 0) and upgrades that
        /// change the rollup derivation. BUMP THIS when a code change means the
        /// existing persisted rollups must be recomputed (PR-C1 = 1: adds the
        /// <c>Unreachable</c> tally, which would otherwise stay 0 on the days
        /// already rolled up by PR-A on a deployed instance).
        /// </summary>
        public const int RollupBackfillVersion = 1;

        public async Task RunCleanupAsync(CancellationToken cancellationToken = default)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var historicalStatusDataRepository = scope.ServiceProvider.GetRequiredService<IHistoricalStatusDataRepository>();
            var webhookExecutionLogRepository = scope.ServiceProvider.GetRequiredService<IWebhookExecutionLogRepository>();
            var alertDeliveryLogRepository = scope.ServiceProvider.GetRequiredService<IAlertDeliveryLogRepository>();
            var dailyStatusRollupRepository = scope.ServiceProvider.GetRequiredService<IDailyStatusRollupRepository>();
            var statusCheckService = scope.ServiceProvider.GetRequiredService<IStatusCheckService>();

            // Issue #107: the webhook audit log keeps the full graph-view window
            // (StatusCheckGraphViewMaxDays). Independent of the raw-tick prune
            // below — #138 deliberately does NOT shrink this to the raw window.
            // Bulk DELETE; fast on the indexed AttemptedUtc DESC.
            int logsDeleted = await webhookExecutionLogRepository
                .BulkDeleteOlderThanXDaysAsync(
                    SuperStatusConfig.StatusCheckGraphViewMaxDays,
                    cancellationToken);
            logger.LogInformation("Cleanup deleted {Count} WebhookExecutionLog rows.", logsDeleted);

            // Issue #241/#253: the alert audit log shares the webhook log's retention
            // posture — keep the full graph-view window.
            int alertLogsDeleted = await alertDeliveryLogRepository
                .BulkDeleteOlderThanXDaysAsync(
                    SuperStatusConfig.StatusCheckGraphViewMaxDays,
                    cancellationToken);
            logger.LogInformation("Cleanup deleted {Count} AlertDeliveryLog rows.", alertLogsDeleted);

            // Issue #138: keep the persisted daily rollups current so the
            // dashboard reads tiny per-day rows instead of aggregating millions
            // of raw ticks. A durable, VERSIONED marker decides between a one-time
            // FULL re-backfill (fresh install → marker 0, or an upgrade that
            // changed the rollup derivation → marker < code version) and the cheap
            // recent-day refresh. The full path recomputes every retained day from
            // raw, so a new column like Unreachable is populated on history that
            // PR-A already rolled up.
            //
            // ORDER MATTERS (PR-C2): the rollup refresh runs BEFORE the raw prune,
            // so every day inside the retained window is captured in a rollup
            // before its raw ticks can be deleted.
            int markerVersion = await dailyStatusRollupRepository.GetBackfillVersionAsync(cancellationToken);
            bool fullBackfill = markerVersion < RollupBackfillVersion;
            int rollupDays = fullBackfill ? SuperStatusConfig.StatusCheckGraphViewMaxDays : 2;
            logger.LogInformation("Refreshing daily rollups ({Days} days{Mode})...", rollupDays, fullBackfill ? ", full backfill" : "");
            await statusCheckService.RefreshDailyRollupsAsync(rollupDays, cancellationToken);
            if (fullBackfill)
            {
                await dailyStatusRollupRepository.SetBackfillVersionAsync(RollupBackfillVersion, cancellationToken);
                logger.LogInformation("Daily-rollup backfill marker advanced to version {Version}.", RollupBackfillVersion);
            }

            // #138 (PR-C2): bound the rollup table itself — drop daily rows beyond
            // the graph-view window so it can't grow without limit.
            int rollupsDeleted = await dailyStatusRollupRepository
                .BulkDeleteOlderThanDaysAsync(SuperStatusConfig.StatusCheckGraphViewMaxDays, cancellationToken);
            if (rollupsDeleted > 0)
                logger.LogInformation("Cleanup deleted {Count} DailyStatusRollup rows beyond the {Days}-day window.", rollupsDeleted, SuperStatusConfig.StatusCheckGraphViewMaxDays);

            // #138 (PR-C2): the small-footprint prune — keep only RawTickRetentionHours
            // (~72 h) of raw ticks; older days are served from the rollups built
            // above. GATED on the backfill marker having been set by a PRIOR tick
            // (markerVersion read at the start ≥ the required version): the prune
            // refuses to run until a full backfill has completed and persisted, so
            // raw is never deleted before its rollups exist. On a fresh instance the
            // first tick backfills + sets the marker but does NOT prune; the next
            // tick prunes. (On an upgrade where PR-C1 already set the marker, the
            // first PR-C2 tick prunes immediately.)
            if (markerVersion >= RollupBackfillVersion)
            {
                int rawDeleted = await historicalStatusDataRepository
                    .BulkDeleteRawOlderThanHoursAsync(SuperStatusConfig.RawTickRetentionHours, cancellationToken: cancellationToken);
                logger.LogInformation("Cleanup pruned {Count} HistoricalStatusData rows older than {Hours} h.", rawDeleted, SuperStatusConfig.RawTickRetentionHours);
            }
            else
            {
                logger.LogInformation("Raw-tick prune deferred: rollups not yet backfilled (marker {Marker} < {Required}); will prune next tick.", markerVersion, RollupBackfillVersion);
            }
        }
    }
}
