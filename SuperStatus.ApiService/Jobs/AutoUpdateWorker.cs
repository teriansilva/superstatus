using Microsoft.Extensions.DependencyInjection;
using SuperStatus.ApiService;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;
using SuperStatus.Services.Updates;

namespace SuperStatus.Scheduler
{
    /// <summary>Issue #334: how often the auto-update worker wakes, and how long after
    /// the scheduled time it will still fire. Injected (rather than hard-coded) so the
    /// loop is unit-testable with sub-second values.</summary>
    /// <param name="Interval">How often a cycle runs.</param>
    /// <param name="CatchUpWindow">
    /// How long today's slot stays open after <c>AutoUpdateTimeUtc</c> passes. It exists
    /// for two reasons: a cycle that missed the exact minute (app restarting, host
    /// asleep) still applies the update, and a trigger that failed transiently gets
    /// retried by the following cycles. It also bounds the surprise: switching
    /// auto-update on at 10:00 with a 03:00 schedule must NOT apply an update
    /// immediately — the console promises "tonight at 03:00 UTC", so a slot that has
    /// already closed waits for tomorrow.
    ///
    /// Must be shorter than 24h: <see cref="AutoUpdateWorker.IsDue"/> considers today's
    /// and yesterday's slot (a late schedule's window crosses midnight), which a window
    /// of a day or more would make overlap.
    /// </param>
    public sealed record AutoUpdateOptions(TimeSpan Interval, TimeSpan CatchUpWindow)
    {
        /// <summary>Default cadence — wake every 5 minutes, keep today's slot open for an hour.</summary>
        public static readonly AutoUpdateOptions Default = new(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
    }

    /// <summary>
    /// Issue #334: applies the daily automatic update. Mirrors <see cref="UpdateCheckWorker"/>
    /// (PeriodicTimer, own DI scope per cycle, non-overlap, clean shutdown), but where
    /// that worker is read-only, this one can restart the stack — so every guard below
    /// is load-bearing.
    ///
    /// The app is the ONLY scheduler: Watchtower ships as an on-demand executor with no
    /// schedule and no polls (docker-compose.watchtower.yml). If the operator's toggle is
    /// off, nothing updates.
    ///
    /// A cycle fires the trigger only when all of these hold:
    ///   * auto-update is enabled by the operator;
    ///   * the update engine is present (opted out ⇒ <c>CanApply</c> is false);
    ///   * the persisted check says an update is actually available;
    ///   * "now" is inside today's slot — [AutoUpdateTimeUtc, +CatchUpWindow);
    ///   * today's slot hasn't already been consumed by an accepted run.
    ///
    /// <c>AutoUpdateLastRunUtc</c> is stamped ONLY when the updater returns
    /// <see cref="UpdateTriggerOutcome.Accepted"/>. A rejected or unreachable trigger
    /// leaves it unset, so the next cycle retries within the same slot rather than
    /// suppressing the update until tomorrow. (The trigger's own 30s cooldown keeps
    /// those retries from hammering Watchtower.)
    /// </summary>
    public sealed class AutoUpdateWorker(
        IServiceScopeFactory scopeFactory,
        AutoUpdateOptions options,
        ILogger<AutoUpdateWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(options.Interval);
            logger.LogInformation(
                "Auto-update worker started ({IntervalMinutes:0.##}m cadence, {WindowMinutes:0.##}m catch-up window).",
                options.Interval.TotalMinutes, options.CatchUpWindow.TotalMinutes);
            try
            {
                do
                {
                    try
                    {
                        await RunCycleAsync(scopeFactory, options, DateTime.UtcNow, logger, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break; // graceful shutdown mid-cycle
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Auto-update cycle failed; continuing.");
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown while awaiting the next tick.
            }
            logger.LogInformation("Auto-update worker stopped.");
        }

        /// <summary>
        /// One cycle. Static + scope-factory-driven (and with "now" injected) so a test
        /// can drive a single cycle at a chosen instant, without the timer.
        /// </summary>
        public static async Task RunCycleAsync(
            IServiceScopeFactory scopeFactory,
            AutoUpdateOptions options,
            DateTime nowUtc,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISiteSettingsService>();

            var auto = await settings.GetAutoUpdateSettingsAsync(cancellationToken);
            if (!auto.Enabled)
                return;

            // Opted out of the update engine (SUPERSTATUS_UPDATE_ENGINE=none): no trigger
            // URL/token on this service, so there is nothing to ask. Never a hard error —
            // the operator may have switched engines with the toggle left on.
            var trigger = scope.ServiceProvider.GetRequiredService<IUpdateTrigger>();
            if (!trigger.CanApply)
                return;

            if (!IsDue(auto, options.CatchUpWindow, nowUtc))
                return;

            // Only apply what the read-only check already established. This worker never
            // reaches for the network to decide whether to restart the stack. Reusing
            // BuildStatus keeps "an update is available" defined in exactly one place —
            // the worker can't disagree with the panel the operator is looking at.
            var version = scope.ServiceProvider.GetRequiredService<IAppVersionProvider>();
            var state = await settings.GetUpdateCheckStateAsync(cancellationToken);
            var status = UpdatesApi.BuildStatus(version.Current, state, auto, trigger.CanApply);
            if (status.Status != UpdateStatusViewModel.StatusUpdateAvailable)
                return;

            var result = await trigger.TriggerAsync(cancellationToken);
            if (result.Accepted)
            {
                // Stamp only now that the updater owns the work. The app is about to be
                // restarted out from under this call.
                await settings.MarkAutoUpdateRunAsync(nowUtc, cancellationToken);
                logger.LogInformation(
                    "Auto-update triggered: current={Current} latest={Latest} scheduled={Scheduled:HH\\:mm}Z",
                    version.Current.Version, state.LatestVersion, auto.TimeUtc);
            }
            else
            {
                // Deliberately NOT stamped: the next cycle inside today's window retries.
                logger.LogWarning(
                    "Auto-update trigger not accepted ({Outcome}); will retry this cycle window. {Error}",
                    result.Outcome, result.Error);
            }
        }

        /// <summary>
        /// Is <paramref name="nowUtc"/> inside a still-unconsumed slot? A slot opens at
        /// <c>TimeUtc</c> and closes <paramref name="catchUpWindow"/> later; an accepted
        /// run inside it consumes it.
        ///
        /// Both today's slot AND yesterday's are considered, because a late schedule's
        /// window crosses midnight: with <c>TimeUtc = 23:30</c> and a 1h window, the slot
        /// that opened at 23:30 is still open at 00:15 the next day. Testing only today's
        /// slot would silently skip that operator's update every night. (Assumes the
        /// window is shorter than a day — see <see cref="AutoUpdateOptions"/>.)
        ///
        /// Pure + unit-tested.
        /// </summary>
        public static bool IsDue(AutoUpdateSettingsDto auto, TimeSpan catchUpWindow, DateTime nowUtc)
        {
            var todaysSlot = nowUtc.Date + auto.TimeUtc.ToTimeSpan();
            return IsInsideOpenSlot(todaysSlot) || IsInsideOpenSlot(todaysSlot.AddDays(-1));

            bool IsInsideOpenSlot(DateTime slotOpens)
            {
                if (nowUtc < slotOpens || nowUtc >= slotOpens + catchUpWindow)
                    return false;

                // Consumed already? Compare against the slot's own opening instant, not
                // the calendar day, so a slot that straddles midnight is judged correctly.
                return auto.LastRunUtc is not { } lastRun || lastRun < slotOpens;
            }
        }
    }
}
