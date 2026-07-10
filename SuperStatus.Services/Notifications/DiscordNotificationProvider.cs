using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// #343 Phase 5: the Discord channel — POSTs <c>{ "content": … }</c> to a Discord
/// <b>webhook</b> URL stored (secret) in the channel's <c>ConfigJson</c>. As with Slack the
/// URL is the credential, so the audit <c>Target</c> is the safe label <c>"discord"</c>.
/// </summary>
public sealed class DiscordNotificationProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<DiscordNotificationProvider> logger) : INotificationProvider
{
    public const string TypeId = NotificationChannelTypes.Discord;

    private static readonly NotificationDescriptor _descriptor = new(
        typeId: TypeId,
        displayName: "Discord",
        icon: "discord",
        description: "Posts alerts to a Discord channel via a webhook.",
        supportsTest: true,
        configSchema: new ConfigSchema(1, new[]
        {
            new ConfigField("url", "Webhook URL", ConfigFieldKind.Secret, Required: true,
                Help: "Discord channel webhook URL (https://discord.com/api/webhooks/…).",
                Placeholder: "https://discord.com/api/webhooks/…"),
        }));

    public NotificationDescriptor Descriptor => _descriptor;

    public async Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        var url = ChannelConfig.Get(context.ConfigJson, "url");
        if (string.IsNullOrWhiteSpace(url))
            return NotificationSendResult.Skipped("no discord webhook url");

        var body = JsonSerializer.Serialize(new { content = ChannelConfig.Message(context) });
        return await ChannelHttp.PostJsonAsync(httpClientFactory, url, body, "discord", logger, cancellationToken);
    }
}
