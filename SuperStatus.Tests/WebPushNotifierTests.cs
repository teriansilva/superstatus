using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Alerts;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #241 Phase C: the Web Push notifier — guard skips (no keys / no devices),
/// real VAPID-signed fan-out against a stub push service, and pruning of endpoints
/// the service reports as gone (404/410).
/// </summary>
[TestClass]
public class WebPushNotifierTests
{
    [TestMethod]
    public async Task NoVapidKeys_isSkipped_notAttempted()
    {
        var (db, conn) = NewDb();
        using var _ = db; using var __ = conn;
        SeedSettings(db, vapid: false);
        AddSub(db, "https://push.test/ok");
        var handler = new RoutingHandler();
        var notifier = Notifier(db, handler);

        var result = await notifier.SendAlertAsync(Check(), AlertTrigger.Failure);

        Assert.AreEqual(WebPushSendStatus.Skipped, result.Status);
        Assert.AreEqual("web push not configured", result.Detail);
        Assert.AreEqual(0, handler.Hits.Count, "nothing is sent when unconfigured");
    }

    [TestMethod]
    public async Task ConfiguredButNoDevices_isSkipped()
    {
        var (db, conn) = NewDb();
        using var _ = db; using var __ = conn;
        SeedSettings(db, vapid: true);
        var notifier = Notifier(db, new RoutingHandler());

        var result = await notifier.SendAlertAsync(Check(), AlertTrigger.Failure);

        Assert.AreEqual(WebPushSendStatus.Skipped, result.Status);
        Assert.AreEqual("no subscribed devices", result.Detail);
    }

    [TestMethod]
    public async Task GoneEndpoint_isPruned_andReportedSkipped()
    {
        var (db, conn) = NewDb();
        using var _ = db; using var __ = conn;
        SeedSettings(db, vapid: true);
        AddSub(db, "https://push.test/gone");
        var notifier = Notifier(db, new RoutingHandler());

        var result = await notifier.SendAlertAsync(Check(), AlertTrigger.Outage);

        Assert.AreEqual(WebPushSendStatus.Skipped, result.Status, "every device expired → not a failure");
        Assert.AreEqual(0, await db.PushSubscriptionSet.CountAsync(), "the 410 endpoint is pruned");
    }

    [TestMethod]
    public async Task LiveAndGone_sendsLive_prunesGone()
    {
        var (db, conn) = NewDb();
        using var _ = db; using var __ = conn;
        SeedSettings(db, vapid: true);
        AddSub(db, "https://push.test/ok");
        AddSub(db, "https://push.test/gone");
        var notifier = Notifier(db, new RoutingHandler());

        var result = await notifier.SendAlertAsync(Check(), AlertTrigger.Failure);

        Assert.AreEqual(WebPushSendStatus.Sent, result.Status);
        StringAssert.Contains(result.Target, "1 device(s)");
        StringAssert.Contains(result.Target, "pruned");
        var remaining = await db.PushSubscriptionSet.Select(x => x.Endpoint).ToListAsync();
        CollectionAssert.AreEqual(new[] { "https://push.test/ok" }, remaining, "only the live endpoint survives");
    }

    [TestMethod]
    public async Task TransientError_isFailed_notPruned()
    {
        var (db, conn) = NewDb();
        using var _ = db; using var __ = conn;
        SeedSettings(db, vapid: true);
        AddSub(db, "https://push.test/boom"); // 500 — a transient error, keep the sub
        var notifier = Notifier(db, new RoutingHandler());

        var result = await notifier.SendAlertAsync(Check(), AlertTrigger.Failure);

        Assert.AreEqual(WebPushSendStatus.Failed, result.Status);
        Assert.AreEqual(1, await db.PushSubscriptionSet.CountAsync(), "a 5xx is not a pruneable 'gone'");
    }

    // ---- helpers ----

    private static (SuperStatusDb db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static void SeedSettings(SuperStatusDb db, bool vapid)
    {
        var s = new SiteSettings { Id = SiteSettings.SingletonId, UpdatedUtc = DateTime.UtcNow };
        if (vapid)
        {
            var keys = WebPush.VapidHelper.GenerateVapidKeys();
            s.VapidPublicKey = keys.PublicKey;
            s.VapidPrivateKey = keys.PrivateKey;
        }
        db.SiteSettingsSet.Add(s);
        db.SaveChanges();
    }

    private static void AddSub(SuperStatusDb db, string endpoint)
    {
        var (p256dh, auth) = NewClientKeys();
        db.PushSubscriptionSet.Add(new PushSubscription
        {
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            CreatedUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static StatusCheck Check() => new()
    {
        Id = 1,
        Title = "svc",
        StatusCheckUrl = "https://svc.test",
        ServiceLogoUrl = string.Empty,
    };

    private static WebPushNotifier Notifier(SuperStatusDb db, HttpMessageHandler handler)
        => new(new SiteSettingsRepository(db), new PushSubscriptionRepository(db),
               new StubFactory(handler), NullLogger<WebPushNotifier>.Instance);

    /// <summary>A genuinely-valid client key pair so the library's payload encryption
    /// succeeds and the request actually reaches the (stub) push service.</summary>
    private static (string p256dh, string auth) NewClientKeys()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdh.ExportParameters(false).Q;
        var uncompressed = new byte[65];
        uncompressed[0] = 0x04;
        Array.Copy(q.X!, 0, uncompressed, 1, 32);
        Array.Copy(q.Y!, 0, uncompressed, 33, 32);
        return (Base64Url(uncompressed), Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public List<string> Hits { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Hits.Add(url);
            var code = url.Contains("/gone") ? HttpStatusCode.Gone
                : url.Contains("/missing") ? HttpStatusCode.NotFound
                : url.Contains("/boom") ? HttpStatusCode.InternalServerError
                : HttpStatusCode.Created;
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
