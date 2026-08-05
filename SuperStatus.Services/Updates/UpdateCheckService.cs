using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SuperStatus.Services.Updates;

/// <summary>The verdict of an update check.</summary>
public enum UpdateStatus
{
    /// <summary>Running the latest release (or an edge/dev build, which we don't nag).</summary>
    UpToDate,
    /// <summary>A newer release than the running version is available.</summary>
    UpdateAvailable,
    /// <summary>The check couldn't be completed (network / rate-limit / parse error).</summary>
    Unknown,
}

/// <summary>
/// Issue #249 (epic #248): the outcome of a single update check. Error-tolerant —
/// a failed check is <see cref="UpdateStatus.Unknown"/> with <see cref="Error"/> set,
/// never an exception into the worker/UI.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseNotesUrl,
    string? Error,
    DateTime CheckedUtc);

/// <summary>Checks whether a newer SuperStatus release is available.</summary>
public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Queries the public GitHub Releases API for the latest release and compares it to
/// the running version. <c>/releases/latest</c> already excludes drafts and
/// pre-releases, so the detection naturally offers only stable releases. Unauthenticated
/// (60 req/hr/IP is ample for a nightly check); any failure is reported as
/// <see cref="UpdateStatus.Unknown"/> rather than thrown.
/// </summary>
public sealed class GitHubUpdateCheckService(
    IHttpClientFactory httpClientFactory,
    IAppVersionProvider versionProvider,
    ILogger<GitHubUpdateCheckService> logger) : IUpdateCheckService
{
    /// <summary>Named <see cref="HttpClient"/> registered in ServiceRegistration.</summary>
    public const string HttpClientName = "github-releases";

    /// <summary>The public OSS mirror that publishes releases.</summary>
    public const string ReleasesLatestPath = "repos/teriansilva/superstatus/releases/latest";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = versionProvider.Current;
        var now = DateTime.UtcNow;

        // Edge/dev builds aren't "behind" a release — report up-to-date without a call.
        if (current.Channel == VersionInfo.ChannelEdge)
            return new UpdateCheckResult(UpdateStatus.UpToDate, current.Version, null, null, null, now);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(ReleasesLatestPath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = $"GitHub releases API returned {(int)response.StatusCode} {response.ReasonPhrase}";
                logger.LogInformation("Update check could not complete: {Error}", error);
                return new UpdateCheckResult(UpdateStatus.Unknown, current.Version, null, null, error, now);
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
            var latest = VersionInfo.Normalize(release?.TagName);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName) || VersionInfo.Compare(latest, "0.0.0") is null)
            {
                const string error = "GitHub releases API returned an unparseable latest version.";
                logger.LogInformation("Update check could not complete: {Error}", error);
                return new UpdateCheckResult(UpdateStatus.Unknown, current.Version, null, null, error, now);
            }

            var status = VersionInfo.IsUpdateAvailable(current.Version, latest)
                ? UpdateStatus.UpdateAvailable
                : UpdateStatus.UpToDate;
            return new UpdateCheckResult(status, current.Version, latest, release.HtmlUrl, null, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Update check could not complete.");
            return new UpdateCheckResult(UpdateStatus.Unknown, current.Version, null, null, ex.Message, now);
        }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}
