using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace SuperStatus.Services.Updates;

/// <summary>The outcome of trying to trigger an in-app update.</summary>
public enum UpdateTriggerOutcome
{
    /// <summary>Watchtower accepted the trigger (the update is now in flight; the app
    /// will restart). Success means accepted, not finished.</summary>
    Accepted,
    /// <summary>One-click apply isn't configured (no Watchtower http-api URL/token).</summary>
    NotConfigured,
    /// <summary>Refused because a trigger was already fired moments ago (anti-spam guard).</summary>
    TooSoon,
    /// <summary>Watchtower rejected the token (401/403) — misconfigured shared secret.</summary>
    Unauthorized,
    /// <summary>Watchtower couldn't be reached / timed out / returned an unexpected status.</summary>
    Unreachable,
}

/// <summary>Result of an apply trigger — the outcome plus a calm, token-free message.</summary>
public sealed record UpdateTriggerResult(UpdateTriggerOutcome Outcome, string? Error)
{
    public bool Accepted => Outcome == UpdateTriggerOutcome.Accepted;
}

/// <summary>
/// Issue #311: triggers an in-app update. The app never touches the Docker socket;
/// it asks Watchtower (which holds the socket in the opt-in overlay) to pull + restart
/// via its authenticated HTTP API. <see cref="CanApply"/> reflects whether that path
/// is configured, so the UI only offers the button when it will work.
/// </summary>
public interface IUpdateTrigger
{
    /// <summary>True when the Watchtower http-api trigger is configured (URL + token).</summary>
    bool CanApply { get; }

    /// <summary>Fire the trigger. Awaits only Watchtower's initial accept/reject under a
    /// short timeout, then returns — it does not track the update across the restart.</summary>
    Task<UpdateTriggerResult> TriggerAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration for <see cref="WatchtowerUpdateTrigger"/>, read from the api service's
/// environment (set only by the opt-in Watchtower overlay). The token is kept here and
/// in the outbound Authorization header only — never serialized to a view model, sent to
/// the browser, or logged.
/// </summary>
public sealed record UpdateTriggerOptions(string? TriggerUrl, string? Token)
{
    /// <summary>Env var carrying the Watchtower http-api update URL (e.g. http://watchtower:8080/v1/update).</summary>
    public const string UrlEnvVar = "SUPERSTATUS_UPDATE_TRIGGER_URL";

    /// <summary>Env var carrying the shared bearer token for Watchtower's http-api.</summary>
    public const string TokenEnvVar = "SUPERSTATUS_UPDATE_TOKEN";

    public static UpdateTriggerOptions FromEnvironment()
        => new(
            Environment.GetEnvironmentVariable(UrlEnvVar),
            Environment.GetEnvironmentVariable(TokenEnvVar));
}

/// <summary>
/// Calls Watchtower's http-api <c>/v1/update</c> with the shared bearer token. Registered
/// as a singleton so its short cooldown is process-wide (the anti-spam guard Hermes asked
/// for): repeated clicks within the cooldown are refused with <see cref="UpdateTriggerOutcome.TooSoon"/>
/// instead of hammering Watchtower.
/// </summary>
public sealed class WatchtowerUpdateTrigger(
    IHttpClientFactory httpClientFactory,
    UpdateTriggerOptions options,
    ILogger<WatchtowerUpdateTrigger> logger) : IUpdateTrigger
{
    /// <summary>Named <see cref="HttpClient"/> registered in ServiceRegistration (short timeout).</summary>
    public const string HttpClientName = "watchtower-trigger";

    /// <summary>Reject a second trigger inside this window — one apply at a time is plenty.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private DateTime _lastTriggeredUtc = DateTime.MinValue;

    public bool CanApply =>
        !string.IsNullOrWhiteSpace(options.TriggerUrl) && !string.IsNullOrWhiteSpace(options.Token);

    public async Task<UpdateTriggerResult> TriggerAsync(CancellationToken cancellationToken = default)
    {
        if (!CanApply)
            return new UpdateTriggerResult(UpdateTriggerOutcome.NotConfigured,
                "In-app updates aren't enabled. Enable Watchtower's update trigger (install.sh --auto-update) or use the command below.");

        // Anti-spam: one in-flight apply per cooldown window. Reserve the slot before
        // the network call so concurrent clicks can't both get through.
        lock (_gate)
        {
            var since = DateTime.UtcNow - _lastTriggeredUtc;
            if (since < Cooldown)
                return new UpdateTriggerResult(UpdateTriggerOutcome.TooSoon,
                    "An update was just requested. Give it a moment to apply.");
            _lastTriggeredUtc = DateTime.UtcNow;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.TriggerUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            using var response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Update trigger accepted by Watchtower ({Status}).", (int)response.StatusCode);
                return new UpdateTriggerResult(UpdateTriggerOutcome.Accepted, null);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Allow an immediate retry after a config fix — don't hold the cooldown on a reject.
                ResetCooldown();
                logger.LogWarning("Update trigger rejected by Watchtower ({Status}).", (int)response.StatusCode);
                return new UpdateTriggerResult(UpdateTriggerOutcome.Unauthorized,
                    "The updater rejected the request (token mismatch). Check the Watchtower configuration.");
            }

            ResetCooldown();
            logger.LogWarning("Update trigger returned an unexpected status from Watchtower ({Status}).", (int)response.StatusCode);
            return new UpdateTriggerResult(UpdateTriggerOutcome.Unreachable,
                $"The updater returned an unexpected response ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Timeout / connection refused / DNS — Watchtower not running or unreachable.
            // Note: messages are deliberately token-free.
            ResetCooldown();
            logger.LogWarning(ex, "Update trigger could not reach Watchtower.");
            return new UpdateTriggerResult(UpdateTriggerOutcome.Unreachable,
                "Couldn't reach the updater. Make sure Watchtower is running, or use the command below.");
        }
    }

    private void ResetCooldown()
    {
        lock (_gate) { _lastTriggeredUtc = DateTime.MinValue; }
    }
}
