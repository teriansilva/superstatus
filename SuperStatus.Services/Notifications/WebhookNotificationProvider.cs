using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Services.Http;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// #343 Phase 4: the outgoing-webhook delivery channel. Replaces the old bare-<c>GET</c>
/// per-check webhook ping (<c>StatusCheckService.FireWebhookAsync</c>) with a first-class
/// channel that <b>POSTs a JSON alert payload</b> to the URL stored in the channel's
/// <c>ConfigJson</c>. Fired by <see cref="Alerts.AlertEvaluator"/> per the profile's alert
/// rules, logged to the unified <c>AlertDeliveryLog</c>.
/// </summary>
public sealed class WebhookNotificationProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookNotificationProvider> logger) : INotificationProvider
{
    public const string TypeId = NotificationChannelTypes.Webhook;
    public const string PayloadJsonKey = "payloadJson";

    private static readonly NotificationDescriptor _descriptor = new(
        typeId: TypeId,
        displayName: "Webhook",
        icon: "webhook",
        description: "POSTs a JSON alert payload to an operator-configured URL.",
        supportsTest: true,
        configSchema: new ConfigSchema(1, new[]
        {
            new ConfigField("url", "Webhook URL", ConfigFieldKind.Text, Required: true,
                Help: "The endpoint the JSON alert payload is POSTed to.",
                Placeholder: "https://example.com/hooks/alerts"),
            new ConfigField(PayloadJsonKey, "Payload JSON", ConfigFieldKind.Json,
                Help: "Optional JSON body template. Leave blank for the default payload. Placeholders: {check}, {url}, {status}, {trigger}, {consecutiveFailures}, {downSinceUtc}, {timestampUtc}.",
                Placeholder: "{\n  \"service\": \"{check}\",\n  \"status\": \"{status}\",\n  \"url\": \"{url}\",\n  \"trigger\": \"{trigger}\",\n  \"timestampUtc\": \"{timestampUtc}\"\n}"),
        }));

    public NotificationDescriptor Descriptor => _descriptor;

    public async Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        var settings = WebhookChannelSettings.FromJson(context.ConfigJson);
        if (string.IsNullOrWhiteSpace(settings.Url))
            return NotificationSendResult.Skipped("no webhook url");

        if (!TryBuildPayload(context, settings.PayloadJson, out var payload, out var payloadError))
            return NotificationSendResult.Failed(settings.Url, payloadError);

        try
        {
            var client = httpClientFactory.CreateClient(StatusCheckHttpClients.Webhook);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(settings.Url, content, cancellationToken);
            int code = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? NotificationSendResult.Sent(settings.Url)
                : NotificationSendResult.Failed(settings.Url, Sanitize($"HTTP {code} {response.ReasonPhrase}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A misbehaving endpoint must never disrupt the tick — return a Failed result.
            // Record the type only (avoid leaking the URL into logs).
            logger.LogWarning("Webhook POST failed ({ExceptionType}).", ex.GetType().Name);
            return NotificationSendResult.Failed(settings.Url, "webhook request failed");
        }
    }

    /// <summary>The stable JSON body a folded webhook posts when no custom payload is
    /// configured. Additive vs. the old bare GET — receivers that ignored the (absent)
    /// body still get pinged, now via POST.</summary>
    internal static string BuildPayload(NotificationContext context)
        => BuildDefaultPayload(context, DateTime.UtcNow);

    internal static bool TryBuildPayload(NotificationContext context, string? payloadTemplateJson, out string payload, out string? error)
    {
        var timestampUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(payloadTemplateJson))
        {
            payload = BuildDefaultPayload(context, timestampUtc);
            error = null;
            return true;
        }

        payload = RenderPayloadTemplate(payloadTemplateJson, context, timestampUtc);
        try
        {
            using var _ = JsonDocument.Parse(payload);
            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = "payload template produced invalid JSON";
            return false;
        }
    }

    private static string BuildDefaultPayload(NotificationContext context, DateTime timestampUtc)
    {
        var check = context.Check;
        bool recovered = context.Trigger == Data.Constants.AlertTrigger.Recovery;
        return JsonSerializer.Serialize(new
        {
            check = check.Title,
            url = check.StatusCheckUrl,
            status = recovered ? "up" : "down",
            trigger = context.Trigger.ToString().ToLowerInvariant(),
            consecutiveFailures = check.ConsecutiveFailures,
            downSinceUtc = check.DownSinceUtc,
            timestampUtc,
        });
    }

    private static string RenderPayloadTemplate(string templateJson, NotificationContext context, DateTime timestampUtc)
    {
        var check = context.Check;
        bool recovered = context.Trigger == Data.Constants.AlertTrigger.Recovery;
        var replacements = new Dictionary<string, string>
        {
            ["check"] = check.Title,
            ["url"] = check.StatusCheckUrl,
            ["status"] = recovered ? "up" : "down",
            ["trigger"] = context.Trigger.ToString().ToLowerInvariant(),
            ["consecutiveFailures"] = check.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture),
            ["downSinceUtc"] = check.DownSinceUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            ["timestampUtc"] = timestampUtc.ToString("O", CultureInfo.InvariantCulture),
        };

        string rendered = templateJson;
        foreach (var (key, value) in replacements)
            rendered = rendered.Replace("{" + key + "}", JsonStringContent(value), StringComparison.Ordinal);
        return rendered;
    }

    private static string JsonStringContent(string value)
    {
        var json = JsonSerializer.Serialize(value ?? string.Empty);
        return json.Length >= 2 ? json[1..^1] : string.Empty;
    }

    private static string Sanitize(string? message)
    {
        var m = (message ?? "webhook failed").Trim();
        return m.Length > AlertDeliveryLog.MaxErrorMessageLength ? m[..AlertDeliveryLog.MaxErrorMessageLength] : m;
    }
}
