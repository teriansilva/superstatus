using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Services.Services;
using SuperStatus.Services.Updates;

namespace SuperStatus.Scheduler
{
    /// <summary>Issue #249: how often the update check runs. Injected (rather than a
    /// hard-coded constant) so the loop is unit-testable with a sub-second interval.</summary>
    public sealed record UpdateCheckOptions(TimeSpan Interval)
    {
        /// <summary>Default cadence — once a day is plenty for a release check.</summary>
        public static readonly UpdateCheckOptions Daily = new(TimeSpan.FromHours(24));
    }

    /// <summary>
    /// Issue #249 (epic #248): the nightly update check. Runs once shortly after
    /// startup then every <see cref="UpdateCheckOptions.Interval"/>, on a
    /// <see cref="PeriodicTimer"/> (same non-overlap + clean-shutdown contract as the
    /// status-check scheduler). Each cycle opens its own DI scope, honours the
    /// operator's <c>UpdateCheckEnabled</c> toggle, calls the GitHub Releases check,
    /// and persists the result. The check is read-only — it never applies an update.
    /// </summary>
    public sealed class UpdateCheckWorker(
        IServiceScopeFactory scopeFactory,
        UpdateCheckOptions options,
        ILogger<UpdateCheckWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(options.Interval);
            logger.LogInformation("Update-check worker started ({IntervalHours:0.##}h cadence).", options.Interval.TotalHours);
            try
            {
                do
                {
                    try
                    {
                        await RunCycleAsync(scopeFactory, logger, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break; // graceful shutdown mid-cycle
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Update check cycle failed; continuing.");
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown while awaiting the next tick.
            }
            logger.LogInformation("Update-check worker stopped.");
        }

        /// <summary>
        /// One check cycle: skip if the operator disabled checks, otherwise run the
        /// check and persist the result. Static + scope-factory-driven so a test can
        /// drive a single cycle without the timer.
        /// </summary>
        public static async Task RunCycleAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISiteSettingsService>();

            var state = await settings.GetUpdateCheckStateAsync(cancellationToken);
            if (!state.Enabled)
                return;

            var checker = scope.ServiceProvider.GetRequiredService<IUpdateCheckService>();
            var result = await checker.CheckAsync(cancellationToken);

            await settings.SetUpdateCheckResultAsync(
                result.LatestVersion, result.ReleaseNotesUrl, result.Error, result.CheckedUtc, cancellationToken);

            logger.LogInformation(
                "Update check: status={Status} current={Current} latest={Latest}{Error}",
                result.Status, result.CurrentVersion, result.LatestVersion ?? "?",
                result.Error is null ? string.Empty : $" error=\"{result.Error}\"");
        }
    }
}
