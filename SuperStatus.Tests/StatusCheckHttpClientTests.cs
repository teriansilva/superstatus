using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services;
using SuperStatus.Services.Http;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #77 — status-check + webhook HTTP plumbing now flows through
/// <see cref="IHttpClientFactory"/> named clients instead of raw
/// <c>new HttpClient()</c>. Asserts (a) the registration wires both named
/// clients with the shared 10 s timeout, and (b) the service resolves the
/// correct named client per use-case while preserving the existing
/// failure-classification + webhook-audit contract (Hermes review).
/// </summary>
[TestClass]
public class StatusCheckHttpClientTests
{
    // ---- registration ----------------------------------------------------

    [TestMethod]
    public void Registration_BothNamedClients_ShareTenSecondTimeout()
    {
        var sp = new ServiceCollection()
            .AddStatusCheckHttpClients()
            .BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        Assert.AreEqual(TimeSpan.FromSeconds(10),
            factory.CreateClient(StatusCheckHttpClients.StatusCheck).Timeout,
            "status-check client must carry the bounded 10 s timeout.");
        Assert.AreEqual(TimeSpan.FromSeconds(10),
            factory.CreateClient(StatusCheckHttpClients.Webhook).Timeout,
            "status-webhook client must carry the bounded 10 s timeout.");
    }

    // ---- ExecuteStatusCheck uses the status-check client ------------------

    [TestMethod]
    public async Task ExecuteStatusCheck_UsesStatusCheckNamedClient_AndClassifies200AsNoFail()
    {
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var check = PersistCheck(db, expectedStatus: 200);
        var factory = new RecordingHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = Service(db, factory);

        var result = await svc.ExecuteStatusCheck(check);

        CollectionAssert.Contains(factory.RequestedNames, StatusCheckHttpClients.StatusCheck);
        Assert.IsFalse(factory.RequestedNames.Contains(StatusCheckHttpClients.Webhook),
            "probing a service must not touch the webhook client.");
        Assert.AreEqual(FailType.NoFail, result.FailType);
        Assert.IsFalse(result.CheckFailed);
    }

    [TestMethod]
    public async Task ExecuteStatusCheck_3xxRecordedAsIs_NotAutoFailed()
    {
        // Hermes #77: redirect behaviour is preserved. A 30x status is
        // recorded verbatim (not coerced to a failure by the client); when
        // it matches the expected code the check is healthy. This pins that
        // the swap to the factory did not change 3xx semantics.
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var check = PersistCheck(db, expectedStatus: 302);
        var factory = new RecordingHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.Redirect));
        var svc = Service(db, factory);

        var result = await svc.ExecuteStatusCheck(check);

        Assert.AreEqual(302, result.HttpStatusCode);
        Assert.AreEqual(FailType.NoFail, result.FailType);
    }

    [TestMethod]
    public async Task ExecuteStatusCheck_TransportFailure_ClassifiedUnreachable()
    {
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var check = PersistCheck(db, expectedStatus: 200);
        var factory = new RecordingHttpClientFactory(
            _ => throw new TaskCanceledException("simulated timeout"));
        var svc = Service(db, factory);

        var result = await svc.ExecuteStatusCheck(check);

        Assert.IsTrue(result.CheckFailed);
        Assert.AreEqual(FailType.Unreachable, result.FailType);
        Assert.AreEqual(0, result.HttpStatusCode);
    }

    // #343 Phase 4: the RunPostStatusCheckTasks webhook-dispatch tests (uses-webhook-
    // client / logs-outcome / returns-action) were removed — webhook delivery is folded
    // into the notification-channel model and fires through AlertEvaluator now, exercised
    // by the Phase-4 webhook-channel tests. RunPostStatusCheckTasks is an inert seam.

    // ---- helpers ----------------------------------------------------------

    private static (SuperStatusDb, SqliteConnection) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck PersistCheck(SuperStatusDb db, int expectedStatus, bool webhook = false)
    {
        var check = new StatusCheck
        {
            Title = "probe",
            StatusCheckUrl = "http://probe.test/health",
            ExpectedStatusCode = expectedStatus,
            Enabled = true,
            ServiceLogoUrl = string.Empty,
            Created = DateTime.UtcNow,
        };
        SlaTestUtil.Attach(check, slowThresholdMs: 60_000);   // #293: classification reads the linked SLA (generous so timing never trips ResponseTime)
        db.StatusCheckSet.Add(check);
        db.SaveChanges();
        // #291 Phase D: dispatch resolves through links only — fixtures link
        // an explicit webhook target (the legacy embedded fields are gone).
        if (webhook) LinkedTargetTestUtil.LinkWebhook(db, check.Id, "http://hook.test/fire");
        return check;
    }

    private static HistoricalStatusData FailedData(StatusCheck check) =>
        new(new StatusCheckResult(check, 0, 0, true), FailType.Unreachable);

    private static StatusCheckService Service(SuperStatusDb db, IHttpClientFactory factory) =>
        new(new StatusCheckRepository(db),
            new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db),
            new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            factory,
            NullLogger<StatusCheckService>.Instance,
            statusCheckLinkRepository: new StatusCheckLinkRepository(db));

    private static async Task<WebhookOutcome> LatestOutcome(SuperStatusDb db) =>
        (await db.WebhookExecutionLogSet.OrderByDescending(l => l.AttemptedUtc).FirstAsync()).Outcome;

    /// <summary>Records the requested client name and serves a stubbed response (or throws).</summary>
    private sealed class RecordingHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return new HttpClient(new FuncHandler(responder)) { Timeout = TimeSpan.FromSeconds(10) };
        }

        private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return f(request);   // throwing here surfaces as a faulted task, exactly like a transport error
            }
        }
    }
}
