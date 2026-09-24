using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Services.Services;

namespace SuperStatus.Scheduler
{
    /// <summary>Issue #168: drains the <see cref="IAutoIncidentQueue"/> and drafts the
    /// auto-incident off the scheduler's hot path, so a slow model never stalls checks.
    /// Each request runs in its own DI scope (own DbContext); the draft service falls
    /// back to a templated incident on any AI failure, and creation is idempotent
    /// (query-before-insert + partial unique index), so a recovered/duplicate request
    /// is a safe no-op.</summary>
    public sealed class AutoIncidentWorker(
        IAutoIncidentQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<AutoIncidentWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var request in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var checks = scope.ServiceProvider.GetRequiredService<IStatusCheckService>();
                    var drafts = scope.ServiceProvider.GetRequiredService<IIncidentDraftService>();
                    var incidents = scope.ServiceProvider.GetRequiredService<IIncidentService>();
                    var coordinator = scope.ServiceProvider.GetRequiredService<IAutoIncidentCoordinator>();

                    var check = await checks.GetStatusCheck(request.CheckId);
                    if (check is null) continue; // deleted between enqueue and now
                    // Re-validate against CURRENT state: a request queued before the
                    // operator disabled AI / the per-check opt-in, or before the check
                    // recovered, must not still publish an incident.
                    if (!await coordinator.ShouldDraftNowAsync(check, stoppingToken)) continue;
                    if (await incidents.HasOpenLinkedAutoIncidentAsync(check.Id, stoppingToken)) continue;

                    IncidentDraft draft = await drafts.DraftAsync(check, request.FailType, stoppingToken);

                    // #415: re-validate existence AFTER the (slow) draft — the operator
                    // may have deleted the check while we were drafting. Correctness is
                    // guaranteed by the DB either way (the SourceStatusCheckId FK, ON
                    // DELETE CASCADE, rejects an insert for a gone check and removes one
                    // committed just before the delete); this cheap skip just avoids a
                    // misleading error-level log for that benign race.
                    if (await checks.GetStatusCheck(request.CheckId) is null) continue; // deleted during draft

                    var created = await incidents.CreateAutoIncidentAsync(check.Id, draft, stoppingToken);
                    logger.LogInformation(
                        "Auto-incident {IncidentId} drafted for check {CheckId} (ai-authored={FromAi}).",
                        created.Id, check.Id, draft.FromAi);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // shutting down
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Auto-incident drafting failed for check {CheckId}.", request.CheckId);
                }
            }
        }
    }
}
