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
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// #343 Phase 4: the folded webhook channel. Covers <see cref="WebhookChannelSettings"/>
/// round-trip, the <see cref="WebhookNotificationProvider"/> payload + POST + result
/// mapping, and the engine firing a webhook channel end-to-end into the unified
/// <see cref="AlertDeliveryLog"/>.
/// </summary>
[TestClass]
public class WebhookChannelTests
{
    // ---- settings round-trip -------------------------------------------------

    [TestMethod]
    public void WebhookChannelSettings_RoundTrips_AndToleratesGarbage()
    {
        const string payload = """{"service":"{check}","state":"{status}"}""";
        var json = new WebhookChannelSettings("https://hook.example/fire", payload).ToJson();
        Assert.AreEqual("https://hook.example/fire", WebhookChannelSettings.FromJson(json).Url);
        Assert.AreEqual(payload, WebhookChannelSettings.FromJson(json).PayloadJson);
        Assert.AreEqual(string.Empty, WebhookChannelSettings.FromJson("""{"url":"https://hook.example/fire"}""").PayloadJson,
            "existing webhook channel rows without payloadJson keep the default payload");
        Assert.AreEqual(WebhookChannelSettings.Empty, WebhookChannelSettings.FromJson("not json"));
        Assert.AreEqual(WebhookChannelSettings.Empty, WebhookChannelSettings.FromJson(null));
    }

    // ---- provider ------------------------------------------------------------

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (WebhookNotificationProvider provider, RecordingHandler handler) Provider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        return (new WebhookNotificationProvider(new Factory(handler), NullLogger<WebhookNotificationProvider>.Instance), handler);
    }

    private static NotificationContext Ctx(string? url, AlertTrigger trigger = AlertTrigger.Failure, string? payloadJson = null, string title = "Public API")
    {
        var check = new StatusCheck { Title = title, StatusCheckUrl = "https://api/health", ServiceLogoUrl = "", ConsecutiveFailures = 3 };
        var configJson = url is null ? null : new WebhookChannelSettings(url, payloadJson ?? string.Empty).ToJson();
        return new NotificationContext(check, trigger, recipientsOverride: null, configJson: configJson);
    }

    [TestMethod]
    public void Descriptor_IsWebhook_AndSupportsTest()
    {
        var (p, _) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.AreEqual("webhook", p.Descriptor.TypeId);
        Assert.IsTrue(p.Descriptor.SupportsTest);
        var payload = p.Descriptor.ConfigSchema.Fields.Single(f => f.Key == WebhookNotificationProvider.PayloadJsonKey);
        Assert.AreEqual(SuperStatus.Services.Plugins.ConfigFieldKind.Json, payload.Kind);
        Assert.IsFalse(payload.Required, "blank payload templates preserve the default webhook body");
    }

    [TestMethod]
    public async Task NoUrl_IsSkipped_NoWireCall()
    {
        var (p, handler) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await p.SendAsync(Ctx(url: null));
        Assert.AreEqual(NotificationOutcome.Skipped, result.Outcome);
        Assert.IsNull(handler.Last, "no request made when the channel has no url");
    }

    [TestMethod]
    public async Task Success_PostsJsonPayload_ReturnsSent()
    {
        var (p, handler) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await p.SendAsync(Ctx("https://hook.example/fire"));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        Assert.AreEqual(HttpMethod.Post, handler.Last!.Method);
        Assert.AreEqual("application/json", handler.Last!.Content!.Headers.ContentType!.MediaType);
        // The payload carries the check + trigger (additive vs. the old bare GET).
        StringAssert.Contains(handler.LastBody!, "Public API");
        StringAssert.Contains(handler.LastBody!, "\"status\":\"down\"");
        StringAssert.Contains(handler.LastBody!, "\"trigger\":\"failure\"");
    }

    [TestMethod]
    public async Task Success_PostsConfiguredPayloadTemplate_RenderedFromContext()
    {
        const string template = """
        {"service":"{check}","state":"{status}","failures":"{consecutiveFailures}","target":"{url}","trigger":"{trigger}"}
        """;
        var (p, handler) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await p.SendAsync(Ctx("https://hook.example/fire", payloadJson: template, title: "Public \"API\"\n{east}"));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.AreEqual("Public \"API\"\n{east}", root.GetProperty("service").GetString(), "placeholder values are JSON-escaped");
        Assert.AreEqual("down", root.GetProperty("state").GetString());
        Assert.AreEqual("3", root.GetProperty("failures").GetString());
        Assert.AreEqual("https://api/health", root.GetProperty("target").GetString());
        Assert.AreEqual("failure", root.GetProperty("trigger").GetString());
    }

    [TestMethod]
    public async Task Success_PostsConfiguredArrayPayloadTemplate()
    {
        const string template = """
        [{"service":"{check}","state":"{status}"}]
        """;
        var (p, handler) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await p.SendAsync(Ctx("https://hook.example/fire", payloadJson: template));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.AreEqual("Public API", doc.RootElement[0].GetProperty("service").GetString());
        Assert.AreEqual("down", doc.RootElement[0].GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task Success_PostsConfiguredStringPayloadTemplate()
    {
        var (p, handler) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await p.SendAsync(Ctx("https://hook.example/fire", payloadJson: "\"{status}\""));

        Assert.AreEqual(NotificationOutcome.Sent, result.Outcome);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.AreEqual(JsonValueKind.String, doc.RootElement.ValueKind);
        Assert.AreEqual("down", doc.RootElement.GetString());
    }

    [TestMethod]
    public async Task InvalidConfiguredPayload_IsContained_NoWireCall()
    {
        var (p, handler) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await p.SendAsync(Ctx("https://hook.example/fire", payloadJson: "{\"service\":\"{check}\""));

        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
        Assert.AreEqual("payload template produced invalid JSON", result.Detail);
        Assert.IsNull(handler.Last, "invalid configured payload is rejected before posting");
    }

    [TestMethod]
    public async Task Recovery_PayloadStatusIsUp()
    {
        var (p, handler) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await p.SendAsync(Ctx("https://hook.example/fire", AlertTrigger.Recovery));
        StringAssert.Contains(handler.LastBody!, "\"status\":\"up\"");
    }

    [TestMethod]
    public async Task NonSuccess_ReturnsFailed()
    {
        var (p, _) = Provider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = "Server Error" });
        var result = await p.SendAsync(Ctx("https://hook.example/fire"));
        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Detail!, "500");
    }

    [TestMethod]
    public async Task TransportError_IsContained_ReturnsFailed()
    {
        var (p, _) = Provider(_ => throw new HttpRequestException("connection refused"));
        var result = await p.SendAsync(Ctx("https://hook.example/fire"));
        Assert.AreEqual(NotificationOutcome.Failed, result.Outcome);
    }

    // ---- engine fires a webhook channel end-to-end ---------------------------

    [TestMethod]
    public async Task Engine_FiresWebhookChannel_LogsWebhookDelivery()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var _c = conn;
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var check = new StatusCheck { Title = "svc", StatusCheckUrl = "https://svc", ServiceLogoUrl = "", AlertOnFailureThreshold = 1 };
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();

        // A profile whose only enabled channel is a webhook (no email/webpush rows).
        var profile = new AlertProfile { Name = "wh", CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.Add(profile);
        await db.SaveChangesAsync();
        db.AlertProfileChannelSet.Add(new AlertProfileChannel
        {
            AlertProfileId = profile.Id,
            ProviderType = NotificationChannelTypes.Webhook,
            IsEnabled = true,
            ConfigJson = new WebhookChannelSettings("https://hook.example/fire").ToJson(),
        });
        db.StatusCheckAlertProfileSet.Add(new StatusCheckAlertProfile { StatusCheckId = check.Id, AlertProfileId = profile.Id });
        await db.SaveChangesAsync();

        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = new NotificationProviderRegistry(new INotificationProvider[]
        {
            new WebhookNotificationProvider(new Factory(handler), NullLogger<WebhookNotificationProvider>.Instance),
        });
        var eval = new AlertEvaluator(new StatusCheckLinkRepository(db), new AlertDeliveryLogRepository(db), registry, NullLogger<AlertEvaluator>.Instance);

        check.ConsecutiveFailures = 1; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        Assert.IsNotNull(handler.Last, "the webhook channel POSTed");
        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(NotificationChannelTypes.Webhook, row.ChannelTypeId, "logged as a webhook delivery in the unified audit");
        Assert.AreEqual(AlertOutcome.Fired, row.Outcome);
    }

    // ---- manual "Run now" fires through the evaluator (Hermes regression) -----

    [TestMethod]
    public async Task RunCheckNow_FiresWebhookChannel_ThroughEvaluator()
    {
        // Hermes review on #359: a manual Run-now must dispatch alerts through the same
        // AlertEvaluator the scheduler uses — so the folded webhook channel fires + logs
        // to AlertDeliveryLog on a manual run, not only on scheduled ticks.
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var _c = conn;
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var check = new StatusCheck
        {
            Title = "svc", StatusCheckUrl = "https://svc/health", ServiceLogoUrl = "",
            ExpectedStatusCode = 200, Enabled = true, AlertOnFailureThreshold = 1, Created = DateTime.UtcNow,
        };
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();

        var profile = new AlertProfile { Name = "wh", CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.Add(profile);
        await db.SaveChangesAsync();
        db.AlertProfileChannelSet.Add(new AlertProfileChannel
        {
            AlertProfileId = profile.Id,
            ProviderType = NotificationChannelTypes.Webhook,
            IsEnabled = true,
            ConfigJson = new WebhookChannelSettings("https://hook.example/fire").ToJson(),
        });
        db.StatusCheckAlertProfileSet.Add(new StatusCheckAlertProfile { StatusCheckId = check.Id, AlertProfileId = profile.Id });
        await db.SaveChangesAsync();

        var webhookHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = new NotificationProviderRegistry(new INotificationProvider[]
        {
            new WebhookNotificationProvider(new Factory(webhookHandler), NullLogger<WebhookNotificationProvider>.Instance),
        });
        var evaluator = new AlertEvaluator(new StatusCheckLinkRepository(db), new AlertDeliveryLogRepository(db), registry, NullLogger<AlertEvaluator>.Instance);

        // The check probe returns 500 (≠ expected 200) → the check fails → threshold 1 met.
        var svc = new StatusCheckService(
            new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new Factory(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))),
            NullLogger<StatusCheckService>.Instance,
            autoIncidentCoordinator: null,
            statusCheckLinkRepository: new StatusCheckLinkRepository(db),
            checkProviderRegistry: null,
            alertEvaluator: evaluator);

        await svc.RunCheckNowAsync(check.Id);

        Assert.IsNotNull(webhookHandler.Last, "manual run fired the webhook channel via the evaluator");
        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(NotificationChannelTypes.Webhook, row.ChannelTypeId);
        Assert.AreEqual(AlertOutcome.Fired, row.Outcome);
    }
}
