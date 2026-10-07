using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// #343 Phase 5: the Telegram channel — POSTs <c>{ "chat_id": …, "text": … }</c> to the Bot
/// API's <c>sendMessage</c> for a bot token + chat id stored in the channel's
/// <c>ConfigJson</c>. The bot token is the credential (it rides the request URL path, never
/// the audit <c>Target</c>); the non-secret chat id is the safe audit label.
/// </summary>
public sealed class TelegramNotificationProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<TelegramNotificationProvider> logger) : INotificationProvider
{
    public const string TypeId = NotificationChannelTypes.Telegram;

    private static readonly NotificationDescriptor _descriptor = new(
        typeId: TypeId,
        displayName: "Telegram",
        icon: "telegram",
        description: "Sends alerts via a Telegram bot to a chat.",
        supportsTest: true,
        configSchema: new ConfigSchema(1, new[]
        {
            new ConfigField("botToken", "Bot token", ConfigFieldKind.Secret, Required: true,
                Help: "Token from @BotFather (123456789:ABC-DEF…)."),
            new ConfigField("chatId", "Chat ID", ConfigFieldKind.Text, Required: true,
                Help: "Numeric chat id, or @channelusername for a public channel.",
                Placeholder: "-1001234567890"),
        }));

    public NotificationDescriptor Descriptor => _descriptor;

    public async Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        var botToken = ChannelConfig.Get(context.ConfigJson, "botToken");
        var chatId = ChannelConfig.Get(context.ConfigJson, "chatId");
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            return NotificationSendResult.Skipped("telegram not configured");

        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        var body = JsonSerializer.Serialize(new { chat_id = chatId, text = ChannelConfig.Message(context) });
        // Audit target = the (non-secret) chat id, never the token-bearing URL.
        return await ChannelHttp.PostJsonAsync(httpClientFactory, url, body, chatId, logger, cancellationToken);
    }
}
