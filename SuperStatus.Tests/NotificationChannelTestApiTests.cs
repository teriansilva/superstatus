using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.ApiService;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Notifications;

namespace SuperStatus.Tests;

/// <summary>
/// #365: the operator-only per-channel test send (<see cref="NotificationChannelTestApi.RunAsync"/>).
/// Covers provider resolution (unknown ⇒ NotFound, non-testable ⇒ rejected), the schema-validation
/// gate (a required secret with nothing typed and nothing stored, or malformed webhook payload
/// JSON, ⇒ InvalidConfig with no wire call), a typed secret on a brand-new channel actually
/// delivering, and the "blank secret reuses the stored credential" rule so an existing channel can
/// be tested without re-typing it.
/// </summary>
[TestClass]
public class NotificationChannelTestApiTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A provider with an arbitrary SupportsTest flag + fixed result — for the resolution
    /// and gate paths that never need a real wire call.</summary>
    private sealed class FakeProvider : INotificationProvider
    {
        private readonly NotificationSendResult _result;
        public FakeProvider(string typeId, bool supportsTest, NotificationSendResult result)
        {
            Descriptor = new NotificationDescriptor(typeId, typeId, "icon", supportsTest: supportsTest);
            _result = result;
        }
        public NotificationDescriptor Descriptor { get; }
        public int Calls { get; private set; }
        public Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private static SuperStatusDb NewDb(SqliteConnection conn)
    {
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static long SeedProfile(SuperStatusDb db)
    {
        var p = new AlertProfile { Name = "p", CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    private static ChannelTestRequest Req(long profileId, params (string Key, string Value)[] config)
    {
        var r = new ChannelTestRequest { ProfileId = profileId };
        foreach (var (k, v) in config) r.Config[k] = v;
        return r;
    }

    private static AlertProfileViewModel Body(string providerType, bool enabled, params (string Key, string Value)[] config)
    {
        var vm = new AlertProfileViewModel { Channels = new() };
        var ch = new AlertProfileChannelViewModel { ProviderType = providerType, IsEnabled = enabled };
        foreach (var (k, v) in config) ch.Config[k] = v;
        vm.Channels.Add(ch);
        return vm;
    }

    [TestMethod]
    public async Task UnknownProvider_IsNotFound()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = new NotificationProviderRegistry(Array.Empty<INotificationProvider>());

        var outcome = await NotificationChannelTestApi.RunAsync("nope", Req(0), registry, repo, default);
        Assert.AreEqual(NotificationChannelTestApi.ChannelTestStatus.UnknownProvider, outcome.Status);
    }

    [TestMethod]
    public async Task NonTestableChannel_IsRejected_NoSend()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var fake = new FakeProvider("webpushy", supportsTest: false, NotificationSendResult.Sent("x"));
        var registry = new NotificationProviderRegistry(new INotificationProvider[] { fake });

        var outcome = await NotificationChannelTestApi.RunAsync("webpushy", Req(0), registry, repo, default);
        Assert.AreEqual(NotificationChannelTestApi.ChannelTestStatus.NotTestable, outcome.Status);
        Assert.AreEqual(0, fake.Calls, "a non-testable channel is never fired");
    }

    [TestMethod]
    public async Task MissingRequiredSecret_NewChannel_IsInvalidConfig_NoWireCall()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var slack = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        var registry = new NotificationProviderRegistry(new INotificationProvider[] { slack });

        var outcome = await NotificationChannelTestApi.RunAsync("slack", Req(0), registry, repo, default);
        Assert.AreEqual(NotificationChannelTestApi.ChannelTestStatus.InvalidConfig, outcome.Status);
        StringAssert.Contains(outcome.Message!, "Slack");
        Assert.IsNull(handler.Last, "an invalid config never reaches the wire");
    }

    [TestMethod]
    public async Task InvalidWebhookPayloadJson_NewChannel_IsInvalidConfig_NoWireCall()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var webhook = new WebhookNotificationProvider(new Factory(handler), NullLogger<WebhookNotificationProvider>.Instance);
        var registry = new NotificationProviderRegistry(new INotificationProvider[] { webhook });

        var outcome = await NotificationChannelTestApi.RunAsync("webhook",
            Req(0,
                ("url", "https://hook.example/fire"),
                (WebhookNotificationProvider.PayloadJsonKey, "{\"service\":\"{check}\"")),
            registry, repo, default);

        Assert.AreEqual(NotificationChannelTestApi.ChannelTestStatus.InvalidConfig, outcome.Status);
        StringAssert.Contains(outcome.Message!, "Payload JSON");
        Assert.IsNull(handler.Last, "an invalid payload template never reaches the wire");
    }

    [TestMethod]
    public async Task TypedSecret_NewChannel_Sends()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var slack = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        var registry = new NotificationProviderRegistry(new INotificationProvider[] { slack });

        var outcome = await NotificationChannelTestApi.RunAsync("slack",
            Req(0, ("url", "https://hooks.slack.com/services/TYPED")), registry, repo, default);

        Assert.AreEqual(NotificationChannelTestApi.ChannelTestStatus.Ok, outcome.Status);
        Assert.AreEqual("sent", outcome.Body!.Outcome);
        Assert.IsTrue(outcome.Body.Ok);
        Assert.IsNotNull(handler.Last, "a freshly-typed webhook URL is delivered without saving");
    }

    [TestMethod]
    public async Task BlankSecret_ExistingChannel_ReusesStoredCredential_Sends()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var slack = new SlackNotificationProvider(new Factory(handler), NullLogger<SlackNotificationProvider>.Instance);
        var registry = new NotificationProviderRegistry(new INotificationProvider[] { slack });
        var profileId = SeedProfile(db);

        // Store a secret, then test with a BLANK config — a missing secret alone is InvalidConfig
        // (proved above), so a successful send here proves the stored credential was reused.
        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Slack, true, ("url", "https://hooks.slack.com/services/STORED")), registry, default);

        var outcome = await NotificationChannelTestApi.RunAsync("slack", Req(profileId), registry, repo, default);

        Assert.AreEqual(NotificationChannelTestApi.ChannelTestStatus.Ok, outcome.Status);
        Assert.AreEqual("sent", outcome.Body!.Outcome);
        Assert.IsNotNull(handler.Last, "the stored webhook URL was used for the test");
    }
}
