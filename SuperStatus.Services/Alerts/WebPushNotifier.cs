using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Services.Alerts;

/// <summary>What happened to a web-push fan-out.</summary>
public enum WebPushSendStatus
{
    /// <summary>Delivered to at least one device.</summary>
    Sent,
    /// <summary>Intentionally not attempted (no VAPID keys / no subscribed devices),
    /// or every subscription was expired and pruned — NOT a delivery failure.</summary>
    Skipped,
    /// <summary>Attempted but every live device errored.</summary>
    Failed,
}

/// <summary>Outcome of a web-push fan-out. <see cref="Skipped"/> (nothing to send /
/// nobody to send to) is distinct from <see cref="Failed"/> (an attempted delivery
/// that errored), mirroring <see cref="EmailSendResult"/>, so the audit log doesn't
/// show an unconfigured channel as a delivery outage.</summary>
public sealed record WebPushSendResult(WebPushSendStatus Status, string Target, string? Detail)
{
    public bool Ok => Status == WebPushSendStatus.Sent;

    public static WebPushSendResult Sent(string target) => new(WebPushSendStatus.Sent, target, null);
    public static WebPushSendResult Skipped(string reason) => new(WebPushSendStatus.Skipped, string.Empty, reason);
    public static WebPushSendResult Failed(string target, string? error) => new(WebPushSendStatus.Failed, target, error);
}

/// <summary>
/// Issue #241 Phase C: fans an alert out to every registered browser Web Push
/// subscription, signed with the operator's VAPID keys. Reads the RAW
/// <see cref="SiteSettings"/> row for the (secret) private key — never the masked
/// view-model. Error-tolerant — returns a result rather than throwing, so a flaky
/// push service produces a Failed audit row, not a crashed tick. Endpoints the push
/// service reports as gone (404/410) are pruned in place so they're never retried.
/// </summary>
public interface IWebPushNotifier
{
    /// <summary>True when VAPID keys exist (a public + private pair).</summary>
    bool IsConfigured(SiteSettings settings);

    /// <summary>Send an alert push for a check + trigger to all subscribed devices.
    /// Returns Skipped (no Target) when web push isn't configured or there are no devices.</summary>
    Task<WebPushSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class WebPushNotifier(
    ISiteSettingsRepository settingsRepository,
    IPushSubscriptionRepository subscriptionRepository,
    IHttpClientFactory httpClientFactory,
    ILogger<WebPushNotifier> logger) : IWebPushNotifier
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client the push sender uses — so
    /// its handler is pooled/configurable and (in tests) stubbable.</summary>
    public const string HttpClientName = "webpush";

    /// <summary>Per-endpoint connect+send budget so a black-holed push service can't hang a tick.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    public bool IsConfigured(SiteSettings settings)
        => !string.IsNullOrWhiteSpace(settings.VapidPublicKey) && !string.IsNullOrWhiteSpace(settings.VapidPrivateKey);

    public async Task<WebPushSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, CancellationToken cancellationToken = default)
    {
        var settings = await settingsRepository.GetSingletonAsync(cancellationToken);
        if (settings is null || !IsConfigured(settings))
            return WebPushSendResult.Skipped("web push not configured");

        var subs = await subscriptionRepository.GetAllAsync(cancellationToken);
        if (subs.Count == 0)
            return WebPushSendResult.Skipped("no subscribed devices");

        var vapid = new WebPush.VapidDetails(VapidSubject(settings), settings.VapidPublicKey, settings.VapidPrivateKey);
        var payload = BuildPayload(check, trigger);

        using var client = new WebPush.WebPushClient(httpClientFactory.CreateClient(HttpClientName));
        int sent = 0, pruned = 0;
        string? lastError = null;

        foreach (var sub in subs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(SendTimeout);

                var pushSub = new WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await client.SendNotificationAsync(pushSub, payload, vapid, cts.Token);
                sent++;
            }
            catch (WebPush.WebPushException ex) when (
                ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.Gone)
            {
                // The push service says this endpoint is gone — drop it so we never retry it.
                await subscriptionRepository.DeleteByEndpointAsync(sub.Endpoint, cancellationToken);
                pruned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One bad endpoint must not abort the fan-out. Record the type only
                // (avoid leaking endpoint/keys), keep a sanitized message for the audit row.
                lastError = Sanitize(ex.Message);
                logger.LogWarning("Web push send failed for one endpoint ({ExceptionType}).", ex.GetType().Name);
            }
        }

        var target = $"{sent} device(s)" + (pruned > 0 ? $", {pruned} pruned" : string.Empty);
        if (sent > 0)
            return WebPushSendResult.Sent(target);
        if (lastError is null)
            // Nothing delivered but nothing errored either — every device had expired.
            return WebPushSendResult.Skipped($"all {pruned} subscription(s) expired (pruned)");
        return WebPushSendResult.Failed(target, lastError);
    }

    /// <summary>VAPID "subject" — a contact the push service can reach about abuse.
    /// Reuses the SMTP from-address when set; otherwise a benign local placeholder.</summary>
    private static string VapidSubject(SiteSettings s)
        => string.IsNullOrWhiteSpace(s.SmtpFromAddress) ? "mailto:alerts@superstatus.local" : $"mailto:{s.SmtpFromAddress}";

    private static string BuildPayload(StatusCheck check, AlertTrigger trigger)
    {
        var (title, body) = trigger == AlertTrigger.Recovery
            ? ($"Recovered: {check.Title}", $"{check.Title} is back up.")
            : ($"{Verb(trigger)}: {check.Title}", $"{check.Title} is failing.");
        // Stable shape consumed by the Phase C2 service worker. `tag` collapses repeat
        // notifications for the same check; `url` is where the notification click lands.
        return JsonSerializer.Serialize(new { title, body, url = "/admin", tag = $"check-{check.Id}" });
    }

    private static string Verb(AlertTrigger trigger) => trigger switch
    {
        AlertTrigger.Recovery => "RECOVERED",
        AlertTrigger.Outage => "OUTAGE",
        _ => "DOWN",
    };

    private static string Sanitize(string? message)
    {
        var m = (message ?? "send failed").Trim();
        return m.Length > AlertDeliveryLog.MaxErrorMessageLength ? m[..AlertDeliveryLog.MaxErrorMessageLength] : m;
    }
}
