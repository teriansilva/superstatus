namespace SuperStatus.Scheduler
{
    /// <summary>
    /// Issue #84: tick intervals for the hosted schedulers, injected (rather
    /// than read from static config inside ExecuteAsync) so the loops are
    /// unit-testable with sub-second intervals. Registered in Program.cs from
    /// SuperStatusConfig.
    /// </summary>
    public sealed record SchedulerIntervals(TimeSpan StatusTick, TimeSpan CleanupTick);

    /// <summary>
    /// Issue #84: drives the status-check tick on a <see cref="PeriodicTimer"/>
    /// instead of Quartz. Runs once at startup (matching Quartz's start-now
    /// trigger + RunJobAtStartup gate) then every JobIntervallInSeconds. Each
    /// tick is fully awaited before the next is scheduled, so ticks never
    /// overlap — the structural replacement for [DisallowConcurrentExecution].
    /// A per-tick exception is logged and the loop continues; cancellation at
    /// shutdown is a clean exit, not an error.
    /// </summary>
    public sealed class StatusCheckSchedulerService(IStatusCheckTick tick, SchedulerIntervals intervals, ILogger<StatusCheckSchedulerService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = intervals.StatusTick;
            using var timer = new PeriodicTimer(interval);
            logger.LogInformation("Status-check scheduler started ({IntervalSeconds}s tick).", interval.TotalSeconds);
            try
            {
                do
                {
                    try
                    {
                        await tick.RunDueChecksAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break; // graceful shutdown mid-tick
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Status-check tick failed; continuing.");
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown while awaiting the next tick — not an error.
            }
            logger.LogInformation("Status-check scheduler stopped.");
        }
    }

    /// <summary>
    /// Issue #84: drives retention cleanup on a <see cref="PeriodicTimer"/>.
    /// Runs once at startup then every DbCleanUpJobIntervallInMinutes; same
    /// non-overlap + clean-shutdown contract as the status-check scheduler.
    /// </summary>
    public sealed class DbCleanupSchedulerService(IDbCleanupTick tick, SchedulerIntervals intervals, ILogger<DbCleanupSchedulerService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = intervals.CleanupTick;
            using var timer = new PeriodicTimer(interval);
            logger.LogInformation("DB-cleanup scheduler started ({IntervalMinutes}m tick).", interval.TotalMinutes);
            try
            {
                do
                {
                    try
                    {
                        await tick.RunCleanupAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "DB-cleanup tick failed; continuing.");
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — not an error.
            }
            logger.LogInformation("DB-cleanup scheduler stopped.");
        }
    }
}
