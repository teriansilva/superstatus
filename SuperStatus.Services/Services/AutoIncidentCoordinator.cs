using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Services.Services
{
    /// <summary>Issue #168: decides, after a check's outcome is recorded, whether a
    /// sustained-downtime auto-incident should be drafted (enqueued) or — on
    /// recovery — resolved. Runs in the scheduler's per-check scope right after
    /// <c>RecordCheckOutcomeAsync</c>; it only ever ENQUEUES the (slow) drafting work
    /// or does a cheap resolve, so the hot path stays fast.</summary>
    public interface IAutoIncidentCoordinator
    {
        /// <param name="wasDown">whether the check had an open down-since BEFORE this
        /// tick was recorded — so recovery work runs only on the down→healthy edge.</param>
        Task EvaluateAsync(StatusCheck check, FailType failType, bool wasDown, CancellationToken cancellationToken = default);

        /// <summary>The drafting gate — per-check opt-in + global AI master switch +
        /// DownSinceUtc age past the (current) threshold. The single source of truth
        /// for both the enqueue decision and the worker's re-validation at draft time
        /// (so a request queued before the operator disabled the feature can't still
        /// publish).</summary>
        Task<bool> ShouldDraftNowAsync(StatusCheck check, CancellationToken cancellationToken = default);
    }

    public sealed class AutoIncidentCoordinator(
        ISiteSettingsRepository settingsRepository,
        IIncidentService incidentService,
        IAutoIncidentQueue queue) : IAutoIncidentCoordinator
    {
        public async Task EvaluateAsync(StatusCheck check, FailType failType, bool wasDown, CancellationToken cancellationToken = default)
        {
            if (failType == FailType.NoFail)
            {
                // Recovery: only on the transition, and only the auto-incident linked
                // to THIS check is resolved — never a manual or unrelated one. Runs
                // regardless of the AI/per-check toggles so a previously-drafted
                // incident always clears when the service comes back.
                if (wasDown)
                {
                    await incidentService.ResolveLinkedAutoIncidentAsync(check.Id, cancellationToken);
                }
                return;
            }

            // Failing: gate on the per-check opt-in + global AI switch + threshold, so
            // with AI off there are zero side-effects (the Phase-1 default).
            if (!await ShouldDraftNowAsync(check, cancellationToken)) return;

            // Skip the enqueue if one is already open (cheap check; the DB partial
            // unique index is the real race guard at insert time).
            if (await incidentService.HasOpenLinkedAutoIncidentAsync(check.Id, cancellationToken)) return;

            queue.TryEnqueue(new AutoIncidentRequest(check.Id, failType));
        }

        public async Task<bool> ShouldDraftNowAsync(StatusCheck check, CancellationToken cancellationToken = default)
        {
            if (!check.AutoIncidentEnabled || check.DownSinceUtc is null) return false;

            var settings = await settingsRepository.GetSingletonAsync(cancellationToken);
            if (settings is not { AiEnabled: true }) return false;

            int threshold = settings.AutoIncidentThresholdMinutes <= 0
                ? SiteSettingsService.DefaultThresholdMinutes
                : settings.AutoIncidentThresholdMinutes;
            return (DateTime.UtcNow - check.DownSinceUtc.Value).TotalMinutes >= threshold;
        }
    }
}
