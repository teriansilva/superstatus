using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Alerts;
using SuperStatus.Services.Notifications;

namespace SuperStatus.Tests;

/// <summary>
/// #343 Phase 5: the chat channels (Slack / Discord / Telegram). Covers each provider's
/// payload + POST + result mapping, the "not configured ⇒ Skipped, no wire call" guard,
/// that a secret URL/token never lands in the audit <c>Target</c>, and the engine firing a
/// Slack channel end-to-end into the unified <see cref="AlertDeliveryLog"/> with a string
/// <see cref="AlertDeliveryLog.ChannelTypeId"/>.
/// </summary>
[TestClass]
public class ChatChannelTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        // Captured here rather than read back off `Last`: the sender disposes the request
        // once the send returns, so the assertion must not depend on post-dispose state.
        public string? LastAuthorization { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastUri = request.RequestUri?.ToString();
            LastMethod = request.Method;
            LastAuthorization = request.Headers.Authorization?.ToString();
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static NotificationContext Ctx(string? configJson, AlertTrigger trigger = AlertTrigger.Failure)
    {
        var check = new StatusCheck { Title = "Public API", StatusCheckUrl = "https://api/health", ServiceLogoUrl = "", ConsecutiveFailures = 3 };
        return new NotificationContext(check, trigger, recipientsOverride: null, configJson: configJson);
    }

    private static string UrlConfig(string url) => JsonSerializer.Serialize(new { url });

    // ---- Slack ---------------------------------------------------------------

    [TestMethod]
    public void Slack_Descriptor_HasSecretUrlField_AndSupportsTest()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        Assert.AreEqual("slack", p.Descriptor.TypeId);
        Assert.IsTrue(p.Descriptor.SupportsTest);
        var url = p.Descriptor.ConfigSchema.Fields.Single(f => f.Key == "url");
        Assert.AreEqual(SuperStatus.Services.Plugins.ConfigFieldKind.Secret, url.Kind, "the webhook URL is a credential");
    }

    [TestMethod]
    public async Task Slack_NoUrl_IsSkipped_NoWireCall()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(configJson: null));
        Assert.AreEqual(NotificationOutcome.Skipped, result.Outcome);
        Assert.IsNull(handler.Last);
    }

    [TestMethod]
    public async Task Slack_Success_PostsTextPayload_TargetIsNotSecretUrl()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(UrlConfig("https://hooks.slack.com/services/SECRET")));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        Assert.AreEqual(HttpMethod.Post, handler.Last!.Method);
        StringAssert.Contains(handler.LastBody!, "\"text\":");
        StringAssert.Contains(handler.LastBody!, "Public API");
        // The secret webhook URL must never surface as the audit target.
        Assert.AreEqual("slack", result.Target);
        Assert.IsFalse((result.Target ?? "").Contains("SECRET"), "secret URL must not leak into the audit target");
    }

    [TestMethod]
    public async Task Slack_NonSuccess_ReturnsFailed()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { ReasonPhrase = "Bad" });
        var p = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(UrlConfig("https://hooks.slack.com/services/x")));
        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Detail!, "400");
    }

    [TestMethod]
    public async Task Slack_TransportError_IsContained_ReturnsFailed()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        var p = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(UrlConfig("https://hooks.slack.com/services/x")));
        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
    }

    // ---- Discord -------------------------------------------------------------

    [TestMethod]
    public async Task Discord_Success_PostsContentPayload()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)); // Discord returns 204
        var p = new DiscordNotificationProvider(new Factory(handler), NullLogger<DiscordNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(UrlConfig("https://discord.com/api/webhooks/1/SECRET")));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        StringAssert.Contains(handler.LastBody!, "\"content\":");
        StringAssert.Contains(handler.LastBody!, "Public API");
        Assert.AreEqual("discord", result.Target);
    }

    [TestMethod]
    public async Task Discord_NoUrl_IsSkipped()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new DiscordNotificationProvider(new Factory(handler), NullLogger<DiscordNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(configJson: null));
        Assert.AreEqual(NotificationOutcome.Skipped, result.Outcome);
        Assert.IsNull(handler.Last);
    }

    // ---- Telegram ------------------------------------------------------------

    private static string TelegramConfig(string botToken, string chatId) => JsonSerializer.Serialize(new { botToken, chatId });

    [TestMethod]
    public void Telegram_Descriptor_HasSecretTokenAndTextChatId()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new TelegramNotificationProvider(new Factory(handler), NullLogger<TelegramNotificationProvider>.Instance);
        Assert.AreEqual("telegram", p.Descriptor.TypeId);
        var token = p.Descriptor.ConfigSchema.Fields.Single(f => f.Key == "botToken");
        var chat = p.Descriptor.ConfigSchema.Fields.Single(f => f.Key == "chatId");
        Assert.AreEqual(SuperStatus.Services.Plugins.ConfigFieldKind.Secret, token.Kind);
        Assert.AreEqual(SuperStatus.Services.Plugins.ConfigFieldKind.Text, chat.Kind);
    }

    [TestMethod]
    public async Task Telegram_Success_CallsBotApi_TokenInUrl_ChatIdIsTarget()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new TelegramNotificationProvider(new Factory(handler), NullLogger<TelegramNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(TelegramConfig("123:ABCSECRET", "-1001")));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        StringAssert.Contains(handler.LastUri!, "https://api.telegram.org/bot123:ABCSECRET/sendMessage");
        StringAssert.Contains(handler.LastBody!, "\"chat_id\":\"-1001\"");
        StringAssert.Contains(handler.LastBody!, "\"text\":");
        // The chat id (non-secret) is the audit target; the token never is.
        Assert.AreEqual("-1001", result.Target);
    }

    [TestMethod]
    public async Task Telegram_MissingToken_IsSkipped_NoWireCall()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new TelegramNotificationProvider(new Factory(handler), NullLogger<TelegramNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(TelegramConfig(botToken: "", chatId: "-1001")));
        Assert.AreEqual(NotificationOutcome.Skipped, result.Outcome);
        Assert.IsNull(handler.Last);
    }

    // ---- Matrix --------------------------------------------------------------

    private const string MatrixRoomId = "!AbCdEf:example.org";

    private static string MatrixConfig(string homeserver = "https://matrix.example.org",
                                       string accessToken = "syt_SECRETTOKEN",
                                       string roomId = MatrixRoomId)
        => JsonSerializer.Serialize(new { homeserver, accessToken, roomId });

    [TestMethod]
    public void Matrix_Descriptor_HasSecretTokenAndTextHomeserverAndRoom()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        Assert.AreEqual("matrix", p.Descriptor.TypeId);
        Assert.IsTrue(p.Descriptor.SupportsTest);
        var homeserver = p.Descriptor.ConfigSchema.Fields.Single(f => f.Key == "homeserver");
        var token = p.Descriptor.ConfigSchema.Fields.Single(f => f.Key == "accessToken");
        var room = p.Descriptor.ConfigSchema.Fields.Single(f => f.Key == "roomId");
        Assert.AreEqual(SuperStatus.Services.Plugins.ConfigFieldKind.Text, homeserver.Kind);
        Assert.AreEqual(SuperStatus.Services.Plugins.ConfigFieldKind.Secret, token.Kind, "the access token is a credential");
        Assert.AreEqual(SuperStatus.Services.Plugins.ConfigFieldKind.Text, room.Kind);
        Assert.IsTrue(homeserver.Required && token.Required && room.Required);
    }

    [TestMethod]
    public async Task Matrix_Success_PutsTextEvent_TokenInHeader_RoomIdIsTarget()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig()));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        // Matrix's send endpoint is a PUT keyed by a per-transaction id.
        Assert.AreEqual(HttpMethod.Put, handler.LastMethod);
        StringAssert.StartsWith(handler.LastUri!,
            "https://matrix.example.org/_matrix/client/v3/rooms/%21AbCdEf%3Aexample.org/send/m.room.message/",
            "room id must be percent-encoded into the path");
        StringAssert.Contains(handler.LastBody!, "\"msgtype\":\"m.text\"");
        StringAssert.Contains(handler.LastBody!, "Public API");

        // The token authenticates via the header — never the URL, target, or anything logged.
        Assert.AreEqual("Bearer syt_SECRETTOKEN", handler.LastAuthorization);
        Assert.IsFalse(handler.LastUri!.Contains("SECRETTOKEN", StringComparison.Ordinal),
            "the access token must never ride the URL (it lands in homeserver access logs)");
        Assert.AreEqual(MatrixRoomId, result.Target);
        Assert.IsFalse((result.Target ?? "").Contains("SECRETTOKEN", StringComparison.Ordinal),
            "the access token must not leak into the audit target");
    }

    [TestMethod]
    public async Task Matrix_TrailingSlashHomeserver_BuildsSingleSlashPath()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig(homeserver: "https://matrix.example.org/")));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        StringAssert.StartsWith(handler.LastUri!, "https://matrix.example.org/_matrix/client/v3/rooms/");
        Assert.IsFalse(handler.LastUri!.Contains("//_matrix", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Matrix_PathPrefixHomeserver_IsPreserved()
    {
        // A homeserver published under a reverse-proxy path prefix.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig(homeserver: "https://example.org/matrix/")));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        StringAssert.StartsWith(handler.LastUri!, "https://example.org/matrix/_matrix/client/v3/rooms/");
    }

    [TestMethod]
    public async Task Matrix_NonDefaultPortHomeserver_IsPreserved()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig(homeserver: "https://matrix.example.org:8448")));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        StringAssert.StartsWith(handler.LastUri!, "https://matrix.example.org:8448/_matrix/client/v3/rooms/");
    }

    [TestMethod]
    public async Task Matrix_HomeserverWithQueryOrFragmentOrUserInfo_FailsBeforeTheWire()
    {
        // Composing by concatenation, any of these would land mid-URL and silently produce a
        // malformed endpoint (or point the bearer token at an unintended authority).
        foreach (var bad in new[]
                 {
                     "https://matrix.example.org/?foo=1",
                     "https://matrix.example.org/#frag",
                     "https://user:pw@matrix.example.org",
                 })
        {
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
            var result = await p.SendAsync(Ctx(MatrixConfig(homeserver: bad)));

            Assert.AreEqual(NotificationOutcome.Failed, result.Outcome, $"'{bad}' must be rejected");
            Assert.IsNull(handler.Last, $"'{bad}' must never reach the wire");
            Assert.AreEqual(MatrixRoomId, result.Target);
        }
    }

    [TestMethod]
    public async Task Matrix_UsesFreshTransactionIdPerSend()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);

        await p.SendAsync(Ctx(MatrixConfig()));
        var first = handler.LastUri;
        await p.SendAsync(Ctx(MatrixConfig()));
        var second = handler.LastUri;

        // Two SendAsync calls are two distinct alerts, so they must carry distinct txn ids:
        // the txn id is Matrix's dedup key, and reusing one would make the homeserver drop
        // the second alert. (Dedup only ever applies to a *retry* reusing the same id — and
        // this channel has no retry policy, so a unique id per call is the whole contract.)
        Assert.AreNotEqual(first, second, "each send must carry its own transaction id");
    }

    [TestMethod]
    public async Task Matrix_NotConfigured_IsSkipped_NoWireCall()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(configJson: null));
        Assert.AreEqual(NotificationOutcome.Skipped, result.Outcome);
        Assert.IsNull(handler.Last);
    }

    [TestMethod]
    public async Task Matrix_MissingToken_IsSkipped_NoWireCall()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig(accessToken: "")));
        Assert.AreEqual(NotificationOutcome.Skipped, result.Outcome);
        Assert.IsNull(handler.Last, "an unconfigured channel must never hit the wire");
    }

    [TestMethod]
    public async Task Matrix_SchemelessHomeserver_FailsWithClearReason_NoWireCall()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig(homeserver: "matrix.example.org")));

        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Detail!, "http(s)");
        Assert.AreEqual(MatrixRoomId, result.Target);
        Assert.IsNull(handler.Last);
    }

    [TestMethod]
    public async Task Matrix_NonSuccess_ReturnsFailed()
    {
        // Synapse answers an unjoined room / bad token with 403 M_FORBIDDEN.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "Forbidden" });
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig()));

        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Detail!, "403");
    }

    [TestMethod]
    public async Task Matrix_HostileReasonPhrase_IsNeverPersistedIntoTheFailureDetail()
    {
        // The homeserver receives the bearer token by design, and `Detail` is written
        // verbatim to AlertDeliveryLog.ErrorMessage and shown in the admin alert log. So an
        // endpoint that echoes the token back in a remote-controlled reason phrase must not
        // be able to copy that credential into the audit database.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "Forbidden: token syt_SECRETTOKEN rejected",
        });
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig()));

        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
        Assert.IsFalse((result.Detail ?? "").Contains("SECRETTOKEN", StringComparison.Ordinal),
            "a remote-controlled reason phrase must never carry the credential into the audit log");
        Assert.IsFalse((result.Detail ?? "").Contains("rejected", StringComparison.Ordinal),
            "no remote-controlled reason text is persisted at all — only the status code");
        StringAssert.Contains(result.Detail!, "403", "the actionable status code is still reported");
    }

    [TestMethod]
    public async Task PostJsonChannels_AlsoDropTheRemoteReasonPhrase()
    {
        // Same invariant on the unauthenticated POST path: the endpoint is operator-supplied
        // there too, so its reason text is equally untrusted for the audit log.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "no_such_channel LEAKY",
        });
        var p = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(UrlConfig("https://hooks.slack.com/services/x")));

        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Detail!, "400");
        Assert.IsFalse((result.Detail ?? "").Contains("LEAKY", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Matrix_TransportError_IsContained_ReturnsFailed()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig()));

        // A dead homeserver is contained as a Failed row; it never reaches the scheduler tick.
        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
        Assert.IsFalse((result.Detail ?? "").Contains("SECRETTOKEN", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Matrix_RecoveryTrigger_SendsRecoveredMessage()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new MatrixNotificationProvider(new Factory(handler), NullLogger<MatrixNotificationProvider>.Instance);
        var result = await p.SendAsync(Ctx(MatrixConfig(), AlertTrigger.Recovery));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        StringAssert.Contains(handler.LastBody!, "Recovered: Public API");
    }

    // ---- ChannelHttp regression: the unauthenticated POST shape is unchanged --

    [TestMethod]
    public async Task PostJsonChannels_StillPostWithoutAuthorizationHeader()
    {
        // #430 routed PostJsonAsync through the new method+bearer core; the three shipped
        // chat channels must keep posting exactly as before — POST, no Authorization.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var p = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        await p.SendAsync(Ctx(UrlConfig("https://hooks.slack.com/services/x")));

        Assert.AreEqual(HttpMethod.Post, handler.LastMethod);
        Assert.IsNull(handler.LastAuthorization, "the webhook URL is the credential; no auth header is sent");
    }

    // ---- engine fires a Slack channel end-to-end -----------------------------

    [TestMethod]
    public async Task Engine_FiresSlackChannel_LogsStringChannelTypeId()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var _c = conn;
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var check = new StatusCheck { Title = "svc", StatusCheckUrl = "https://svc", ServiceLogoUrl = "", AlertOnFailureThreshold = 1 };
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();

        var profile = new AlertProfile { Name = "chat", CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.Add(profile);
        await db.SaveChangesAsync();
        db.AlertProfileChannelSet.Add(new AlertProfileChannel
        {
            AlertProfileId = profile.Id,
            ProviderType = NotificationChannelTypes.Slack,
            IsEnabled = true,
            ConfigJson = UrlConfig("https://hooks.slack.com/services/SECRET"),
        });
        db.StatusCheckAlertProfileSet.Add(new StatusCheckAlertProfile { StatusCheckId = check.Id, AlertProfileId = profile.Id });
        await db.SaveChangesAsync();

        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = new NotificationProviderRegistry(new INotificationProvider[]
        {
            new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance),
        });
        var eval = new AlertEvaluator(new StatusCheckLinkRepository(db), new AlertDeliveryLogRepository(db), registry, NullLogger<AlertEvaluator>.Instance);

        check.ConsecutiveFailures = 1; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        Assert.IsNotNull(handler.Last, "the slack channel POSTed");
        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual("slack", row.ChannelTypeId, "audit logs the raw channel type id string");
        Assert.AreEqual(AlertOutcome.Fired, row.Outcome);
        Assert.AreEqual("slack", row.Target, "the secret webhook URL is never stored as the audit target");
    }
}
