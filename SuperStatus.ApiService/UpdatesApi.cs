using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;
using SuperStatus.Services.Updates;

namespace SuperStatus.ApiService;

/// <summary>
/// Issue #249 (epic #248): the operator-console update endpoints — read state +
/// trigger an on-demand check. #311 added <c>POST /api/updates/apply</c>, which asks
/// Watchtower (via its authenticated http-api) to pull + restart. #334 added
/// <c>POST /api/updates/auto</c>, which persists the auto-update toggle + daily UTC
/// time, so day-2 updates never need server access.
///
/// The app itself still never touches the Docker socket. The api service isn't
/// internet-exposed; the mutating endpoints still require the operator token, and no
/// response or error ever carries the updater token.
/// </summary>
public static class UpdatesApi
{
    public static void MapUpdatesApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/updates", async (
            IAppVersionProvider version,
            ISiteSettingsService settings,
            IUpdateTrigger trigger,
            CancellationToken ct) =>
        {
            var state = await settings.GetUpdateCheckStateAsync(ct);
            var auto = await settings.GetAutoUpdateSettingsAsync(ct);
            return Results.Json(BuildStatus(version.Current, state, auto, trigger.CanApply));
        });

        // Trigger an immediate check and persist it, then return the fresh status.
        app.MapPost("/api/updates/check", async (
            IAppVersionProvider version,
            ISiteSettingsService settings,
            IUpdateCheckService checker,
            IUpdateTrigger trigger,
            CancellationToken ct) =>
        {
            var result = await checker.CheckAsync(ct);
            await settings.SetUpdateCheckResultAsync(
                result.LatestVersion, result.ReleaseNotesUrl, result.Error, result.CheckedUtc, ct);
            var state = await settings.GetUpdateCheckStateAsync(ct);
            var auto = await settings.GetAutoUpdateSettingsAsync(ct);
            return Results.Json(BuildStatus(version.Current, state, auto, trigger.CanApply));
        }).RequireAuthorization();

        // Issue #334: persist the auto-update policy. Strict "HH:mm" so a malformed
        // time is rejected rather than silently coerced to midnight — which would
        // quietly move an operator's 03:00 window into the middle of their day.
        app.MapPost("/api/updates/auto", async (
            AutoUpdateRequest request,
            IAppVersionProvider version,
            ISiteSettingsService settings,
            IUpdateTrigger trigger,
            CancellationToken ct) =>
        {
            if (!AutoUpdateRequest.TryParseTime(request.Time, out var timeUtc))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(AutoUpdateRequest.Time)] = ["Time must be a 24-hour UTC time of day, formatted HH:mm (e.g. 03:00)."],
                });
            }

            var auto = await settings.SetAutoUpdateSettingsAsync(request.Enabled, timeUtc, ct);
            var state = await settings.GetUpdateCheckStateAsync(ct);
            return Results.Json(BuildStatus(version.Current, state, auto, trigger.CanApply));
        }).RequireAuthorization();

        // Issue #311: apply the update now — ask Watchtower's http-api to pull + restart.
        // Awaits only the initial accept/reject (the trigger's HttpClient has a short
        // timeout), then returns; the update restarts the app out from under this call.
        // Success = trigger accepted, not update finished.
        app.MapPost("/api/updates/apply", async (
            IUpdateTrigger trigger,
            CancellationToken ct) =>
        {
            var result = await trigger.TriggerAsync(ct);
            var dto = new UpdateApplyResult { Accepted = result.Accepted, Error = result.Error };
            var statusCode = result.Outcome switch
            {
                UpdateTriggerOutcome.Accepted => StatusCodes.Status202Accepted,
                UpdateTriggerOutcome.NotConfigured => StatusCodes.Status409Conflict,
                UpdateTriggerOutcome.TooSoon => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status502BadGateway, // Unauthorized / Unreachable
            };
            return Results.Json(dto, statusCode: statusCode);
        }).RequireAuthorization();
    }

    /// <summary>
    /// Compose the view model. The comparison verdict is computed from the
    /// last-known-good latest version (so a transient check failure still shows
    /// the prior verdict); a failed check surfaces separately via
    /// <see cref="UpdateStatusViewModel.LastCheckError"/>. Pure + unit-tested, and
    /// reused by <c>AutoUpdateWorker</c> so the worker's notion of "an update is
    /// available" is exactly the panel's.
    /// </summary>
    public static UpdateStatusViewModel BuildStatus(
        AppVersionInfo current,
        UpdateCheckStateDto state,
        AutoUpdateSettingsDto auto,
        bool canApplyInApp = false)
    {
        string status =
            current.Channel == VersionInfo.ChannelEdge ? UpdateStatusViewModel.StatusEdge
            : state.LatestVersion is null ? UpdateStatusViewModel.StatusUnknown
            : VersionInfo.IsUpdateAvailable(current.Version, state.LatestVersion)
                ? UpdateStatusViewModel.StatusUpdateAvailable
                : UpdateStatusViewModel.StatusUpToDate;

        return new UpdateStatusViewModel
        {
            CurrentVersion = current.Version,
            Channel = current.Channel,
            Status = status,
            CheckEnabled = state.Enabled,
            LastCheckedUtc = state.LastCheckedUtc,
            LatestVersion = state.LatestVersion,
            LatestNotesUrl = state.LatestNotesUrl,
            LastCheckError = state.LastCheckError,
            AutoUpdateEnabled = auto.Enabled,
            AutoUpdateTimeUtc = auto.TimeUtc,
            AutoUpdateLastRunUtc = auto.LastRunUtc,
            CanApplyInApp = canApplyInApp,
        };
    }
}
