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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastUri = request.RequestUri?.ToString();
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
