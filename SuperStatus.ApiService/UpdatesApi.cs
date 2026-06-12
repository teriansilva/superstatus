using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;
using SuperStatus.Services.Updates;

namespace SuperStatus.ApiService;

/// <summary>
/// Issue #249 (epic #248): the operator-console update endpoints. Read state +
/// trigger an on-demand check. Read-only — never applies an update (that's the
/// opt-in Watchtower overlay, Phase 2). The api service isn't internet-exposed;
/// the mutating "check now" still requires the operator token.
/// </summary>
public static class UpdatesApi
{
    /// <summary>Env flag the optional Watchtower overlay sets to advertise that
    /// automatic updates are active (display only).</summary>
    public const string AutoUpdateEnvVar = "SUPERSTATUS_AUTOUPDATE";

    public static void MapUpdatesApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/updates", async (
            IAppVersionProvider version,
            ISiteSettingsService settings,
            CancellationToken ct) =>
        {
            var state = await settings.GetUpdateCheckStateAsync(ct);
            return Results.Json(BuildStatus(version.Current, state, AutoUpdateActive()));
        });

        // Trigger an immediate check and persist it, then return the fresh status.
        app.MapPost("/api/updates/check", async (
            IAppVersionProvider version,
            ISiteSettingsService settings,
            IUpdateCheckService checker,
            CancellationToken ct) =>
        {
            var result = await checker.CheckAsync(ct);
            await settings.SetUpdateCheckResultAsync(
                result.LatestVersion, result.ReleaseNotesUrl, result.Error, result.CheckedUtc, ct);
            var state = await settings.GetUpdateCheckStateAsync(ct);
            return Results.Json(BuildStatus(version.Current, state, AutoUpdateActive()));
        }).RequireAuthorization();
    }

    private static bool AutoUpdateActive()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AutoUpdateEnvVar));

    /// <summary>
    /// Compose the view model. The comparison verdict is computed from the
    /// last-known-good latest version (so a transient check failure still shows
    /// the prior verdict); a failed check surfaces separately via
    /// <see cref="UpdateStatusViewModel.LastCheckError"/>. Pure + unit-tested.
    /// </summary>
    public static UpdateStatusViewModel BuildStatus(AppVersionInfo current, UpdateCheckStateDto state, bool autoUpdateActive)
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
            AutoUpdateActive = autoUpdateActive,
        };
    }
}
