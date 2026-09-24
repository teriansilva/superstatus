using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// #430: the Matrix channel — sends an <c>m.room.message</c> event into a room on any
/// Matrix homeserver (Synapse and friends) via the client-server API:
/// <c>PUT {homeserver}/_matrix/client/v3/rooms/{roomId}/send/m.room.message/{txnId}</c>.
/// <para>
/// The bot's access token is the credential and rides the <c>Authorization</c> header —
/// never the URL (the deprecated <c>?access_token=</c> form lands in homeserver access
/// logs) and never the audit <c>Target</c>, which is the non-secret room id. Same split as
/// Telegram's token-vs-chat-id.
/// </para>
/// </summary>
public sealed class MatrixNotificationProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<MatrixNotificationProvider> logger) : INotificationProvider
{
    public const string TypeId = NotificationChannelTypes.Matrix;

    private static readonly NotificationDescriptor _descriptor = new(
        typeId: TypeId,
        displayName: "Matrix",
        icon: "matrix",
        description: "Posts alerts into a Matrix room via a bot's access token.",
        supportsTest: true,
        configSchema: new ConfigSchema(1, new[]
        {
            new ConfigField("homeserver", "Homeserver URL", ConfigFieldKind.Text, Required: true,
                Help: "Base URL of the homeserver's client-server API. A path prefix is allowed; a query or fragment is not.",
                Placeholder: "https://matrix.example.org"),
            new ConfigField("accessToken", "Access token", ConfigFieldKind.Secret, Required: true,
                Help: "Access token of the bot user that posts the alerts (Element → Settings → Help & About → Advanced)."),
            new ConfigField("roomId", "Room ID", ConfigFieldKind.Text, Required: true,
                Help: "Internal room ID, not an alias (Element → Room settings → Advanced). The room must be unencrypted and the bot must have joined it.",
                Placeholder: "!AbCdEfGhIjK:example.org"),
        }));

    public NotificationDescriptor Descriptor => _descriptor;

    public async Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        var homeserver = ChannelConfig.Get(context.ConfigJson, "homeserver").Trim();
        var accessToken = ChannelConfig.Get(context.ConfigJson, "accessToken");
        var roomId = ChannelConfig.Get(context.ConfigJson, "roomId").Trim();
        if (string.IsNullOrWhiteSpace(homeserver) || string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(roomId))
            return NotificationSendResult.Skipped("matrix not configured");

        // A scheme-less host would otherwise fail deep in the HTTP stack as an opaque
        // "request failed"; say what's actually wrong instead. The room id is the safe target.
        if (!Uri.TryCreate(homeserver, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return NotificationSendResult.Failed(roomId, "homeserver must be an absolute http(s) URL");
        }

        // The endpoint is composed from the parsed authority + base path, so only those two
        // parts of the operator's value can shape the request. A query / fragment would land
        // mid-URL and silently produce a malformed endpoint; user-info is a credential in the
        // wrong place (the bearer token is the credential). Reject all three before the wire.
        if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            return NotificationSendResult.Failed(roomId, "homeserver must not carry a query, fragment, or user info");
        }

        // Room ids carry '!' and ':' (aliases '#'), none of which are path-safe.
        var encodedRoomId = Uri.EscapeDataString(roomId);
        // Matrix's idempotency key. It is unique per delivery attempt, which is what this
        // channel needs: there is no application-level retry here — the engine calls
        // SendAsync once per alert — so every send is a genuinely distinct event and must
        // carry a distinct id, or the homeserver would drop the second alert as a duplicate.
        // If a retry policy is ever added, it must allocate ONE txn id per logical delivery
        // and reuse it across that delivery's retries; only then does the id deduplicate.
        var txnId = Guid.NewGuid().ToString("N");
        // A base path is supported (homeservers behind a reverse-proxy prefix); the trailing
        // slash is normalised away so the composed path never doubles up.
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var url = $"{baseUri.GetLeftPart(UriPartial.Authority)}{basePath}"
                  + $"/_matrix/client/v3/rooms/{encodedRoomId}/send/m.room.message/{txnId}";

        var body = JsonSerializer.Serialize(new { msgtype = "m.text", body = ChannelConfig.Message(context) });
        return await ChannelHttp.SendJsonAsync(
            httpClientFactory, HttpMethod.Put, url, body, roomId, accessToken, logger, cancellationToken);
    }
}
