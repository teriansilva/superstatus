using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// #343 Phase 5: the Slack channel — POSTs <c>{ "text": … }</c> to a Slack
/// <b>incoming-webhook</b> URL stored (secret) in the channel's <c>ConfigJson</c>. The URL
/// itself is the credential, so it never rides the audit <c>Target</c> — the safe label
/// <c>"slack"</c> does.
/// </summary>
public sealed class SlackNotificationProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<SlackNotificationProvider> logger) : INotificationProvider
{
    public const string TypeId = NotificationChannelTypes.Slack;

    private static readonly NotificationDescriptor _descriptor = new(
        typeId: TypeId,
        displayName: "Slack",
        icon: "slack",
        description: "Posts alerts to a Slack channel via an incoming webhook.",
        supportsTest: true,
        configSchema: new ConfigSchema(1, new[]
        {
            new ConfigField("url", "Incoming webhook URL", ConfigFieldKind.Secret, Required: true,
                Help: "Slack incoming-webhook URL (https://hooks.slack.com/services/…).",
                Placeholder: "https://hooks.slack.com/services/…"),
        }));

    public NotificationDescriptor Descriptor => _descriptor;

    public async Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        var url = ChannelConfig.Get(context.ConfigJson, "url");
        if (string.IsNullOrWhiteSpace(url))
            return NotificationSendResult.Skipped("no slack webhook url");

        var body = JsonSerializer.Serialize(new { text = ChannelConfig.Message(context) });
        return await ChannelHttp.PostJsonAsync(httpClientFactory, url, body, "slack", logger, cancellationToken);
    }
}
