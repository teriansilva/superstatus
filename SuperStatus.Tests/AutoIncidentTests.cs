using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #168 Phase 2 — the draft service (AI success / fallback / disabled),
/// the sustained-downtime coordinator (threshold / dedup / disabled / recovery),
/// and the idempotent auto-incident create/resolve in IncidentService.
/// </summary>
[TestClass]
public class AutoIncidentTests
{
    // ---------- harness ----------

    private static (SuperStatusDb db, SqliteConnection conn) Db()
    {
        var conn = new SqliteConnection("Filename=:memory:"); conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(request));
    }

    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) { Calls++; return new HttpClient(new StubHandler(responder)); }
    }

    private sealed class FakeQueue : IAutoIncidentQueue
    {
        public readonly List<AutoIncidentRequest> Enqueued = new();
        public bool TryEnqueue(AutoIncidentRequest request) { Enqueued.Add(request); return true; }
        public async IAsyncEnumerable<AutoIncidentRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
    }

    private static HttpResponseMessage ChatJson(string innerContent)
    {
        // OpenAI-compatible envelope: choices[0].message.content is a JSON string.
        string body = $"{{\"choices\":[{{\"message\":{{\"content\":{JsonSerializer.Serialize(innerContent)}}}}}]}}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static async Task SeedSettings(SuperStatusDb db, bool aiEnabled, int thresholdMin = 5)
    {
        db.SiteSettingsSet.Add(new SiteSettings
        {
            Id = SiteSettings.SingletonId, AccentColor = "#3fbf6f",
            AiEnabled = aiEnabled, AiBaseUrl = "https://gw/v1", AiModel = "m", AiApiKey = "sk-x",
            AiTimeoutSeconds = 20, AutoIncidentThresholdMinutes = thresholdMin,
        });
        await db.SaveChangesAsync();
    }

    private static StatusCheck DownCheck(long id, int downMinutes, bool optIn = true) => new()
    {
        Id = id, Title = "API", StatusCheckUrl = "https://api/health", ServiceLogoUrl = "",
        Enabled = true, AutoIncidentEnabled = optIn, ConsecutiveFailures = 3,
        DownSinceUtc = DateTime.UtcNow.AddMinutes(-downMinutes), Created = DateTime.UtcNow,
    };

    // ---------- draft service ----------

    [TestMethod]
    public async Task Draft_AiSuccess_ParsesTitleDescriptionSeverity()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true);
        var factory = new StubFactory(_ => ChatJson("{\"title\":\"API is down\",\"description\":\"The API is unreachable.\",\"severity\":\"Severe\"}"));
        var svc = new IncidentDraftService(new SiteSettingsRepository(db), factory, NullLogger<IncidentDraftService>.Instance);

        var draft = await svc.DraftAsync(DownCheck(1, 10), FailType.Unreachable);

        Assert.IsTrue(draft.FromAi);
        Assert.AreEqual("API is down", draft.Title);
        Assert.AreEqual("The API is unreachable.", draft.Description);
        Assert.AreEqual(IncidentSeverity.Severe, draft.Severity);
    }

    [TestMethod]
    public async Task Draft_AiHttpError_FallsBackToTemplated()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true);
        var factory = new StubFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var svc = new IncidentDraftService(new SiteSettingsRepository(db), factory, NullLogger<IncidentDraftService>.Instance);

        var draft = await svc.DraftAsync(DownCheck(1, 10), FailType.Unreachable);

        Assert.IsFalse(draft.FromAi, "HTTP failure → templated fallback");
        StringAssert.Contains(draft.Title, "API");
        Assert.AreEqual(2, factory.Calls, "single retry → two attempts");
    }

    [TestMethod]
    public async Task Draft_AiTimeout_FallsBackToTemplated()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true);
        // A timeout surfaces as TaskCanceledException (the per-request CTS firing) —
        // it must be caught and fall back, not propagate.
        var factory = new StubFactory(_ => throw new TaskCanceledException("timeout"));
        var svc = new IncidentDraftService(new SiteSettingsRepository(db), factory, NullLogger<IncidentDraftService>.Instance);

        var draft = await svc.DraftAsync(DownCheck(1, 10), FailType.ResponseTime);

        Assert.IsFalse(draft.FromAi);
        StringAssert.Contains(draft.Description, "slower than the configured threshold");
    }

    [TestMethod]
    public async Task Draft_AiUnparseable_FallsBackToTemplated()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true);
        var factory = new StubFactory(_ => ChatJson("this is not json"));
        var svc = new IncidentDraftService(new SiteSettingsRepository(db), factory, NullLogger<IncidentDraftService>.Instance);

        var draft = await svc.DraftAsync(DownCheck(1, 10), FailType.StatusCode);

        Assert.IsFalse(draft.FromAi);
        Assert.AreEqual(1, factory.Calls, "an unparseable 200 is not retried");
    }

    [TestMethod]
    public async Task Draft_AiDisabled_UsesTemplated_NoHttpCall()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: false);
        var factory = new StubFactory(_ => throw new InvalidOperationException("must not call the model when AI is disabled"));
        var svc = new IncidentDraftService(new SiteSettingsRepository(db), factory, NullLogger<IncidentDraftService>.Instance);

        var draft = await svc.DraftAsync(DownCheck(1, 10), FailType.Unreachable);

        Assert.IsFalse(draft.FromAi);
        Assert.AreEqual(0, factory.Calls, "no HTTP call when AI is disabled");
    }

    [TestMethod]
    public async Task Draft_RedactsUrlCredentials_FromPublicCopy()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: false); // templated path → public fallback copy
        var factory = new StubFactory(_ => throw new InvalidOperationException("AI disabled"));
        var svc = new IncidentDraftService(new SiteSettingsRepository(db), factory, NullLogger<IncidentDraftService>.Instance);

        var check = new StatusCheck
        {
            Id = 1, Title = "API", ServiceLogoUrl = "", Enabled = true, AutoIncidentEnabled = true,
            StatusCheckUrl = "https://user:hunter2@api.example.com/health?token=topsecret",
            DownSinceUtc = DateTime.UtcNow.AddMinutes(-10), Created = DateTime.UtcNow,
        };

        var draft = await svc.DraftAsync(check, FailType.Unreachable);

        StringAssert.Contains(draft.Description, "https://api.example.com/health", "scheme/host/path kept");
        Assert.IsFalse(draft.Description.Contains("hunter2"), "userinfo password redacted");
        Assert.IsFalse(draft.Description.Contains("topsecret"), "query token redacted");
        Assert.IsFalse(draft.Description.Contains("user:"), "userinfo dropped");
    }

    [TestMethod]
    public async Task ShouldDraftNow_RevalidatesCurrentState()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true, thresholdMin: 5);
        var (coord, _, _) = Coord(db);

        Assert.IsTrue(await coord.ShouldDraftNowAsync(DownCheck(1, 10)), "down past threshold + AI on + opted in");
        Assert.IsFalse(await coord.ShouldDraftNowAsync(DownCheck(1, 1)), "below threshold");
        Assert.IsFalse(await coord.ShouldDraftNowAsync(DownCheck(1, 10, optIn: false)), "per-check opt-out");

        // Operator disables AI while a request is queued → the gate now returns false,
        // so the worker won't publish a stale request.
        db.SiteSettingsSet.Single().AiEnabled = false; await db.SaveChangesAsync();
        Assert.IsFalse(await coord.ShouldDraftNowAsync(DownCheck(1, 10)), "AI disabled mid-flight");
    }

    // ---------- IncidentService idempotency + resolve ----------

    [TestMethod]
    public async Task CreateAutoIncident_IsIdempotent_OneOpenPerCheck()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        var svc = new IncidentService(new IncidentRepository(db));
        var draft = new IncidentDraft("t", "d", IncidentSeverity.Minor, false);

        var first = await svc.CreateAutoIncidentAsync(7, draft);
        var second = await svc.CreateAutoIncidentAsync(7, draft);

        Assert.AreEqual(first.Id, second.Id, "second call reuses the open incident");
        Assert.AreEqual(1, await db.IncidentSet.CountAsync(i => i.SourceStatusCheckId == 7));
        Assert.IsTrue(await svc.HasOpenLinkedAutoIncidentAsync(7));
    }

    [TestMethod]
    public async Task ResolveLinkedAutoIncident_ResolvesOnlyTheLinkedAuto()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        var svc = new IncidentService(new IncidentRepository(db));
        await svc.CreateAutoIncidentAsync(7, new IncidentDraft("auto", "d", IncidentSeverity.Minor, false));
        // a manual incident must be untouched
        db.IncidentSet.Add(new Incident { Title = "manual", AuotmaticallyGeneratedReport = false, Created = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await svc.ResolveLinkedAutoIncidentAsync(7);

        var auto = await db.IncidentSet.SingleAsync(i => i.SourceStatusCheckId == 7);
        Assert.IsTrue(auto.Resolved && auto.ResolvedUtc != null, "linked auto-incident resolved + stamped");
        var manual = await db.IncidentSet.SingleAsync(i => i.Title == "manual");
        Assert.IsFalse(manual.Resolved, "manual incident untouched");

        // No-op when nothing open (must not throw).
        await svc.ResolveLinkedAutoIncidentAsync(7);
    }

    // ---------- coordinator ----------

    private static (AutoIncidentCoordinator coord, FakeQueue queue, IncidentService incidents) Coord(SuperStatusDb db)
    {
        var incidents = new IncidentService(new IncidentRepository(db));
        var queue = new FakeQueue();
        var coord = new AutoIncidentCoordinator(new SiteSettingsRepository(db), incidents, queue);
        return (coord, queue, incidents);
    }

    [TestMethod]
    public async Task Coordinator_EnqueuesWhenThresholdCrossed()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true, thresholdMin: 5);
        var (coord, queue, _) = Coord(db);

        await coord.EvaluateAsync(DownCheck(1, downMinutes: 10), FailType.Unreachable, wasDown: true);

        Assert.AreEqual(1, queue.Enqueued.Count);
        Assert.AreEqual(1, queue.Enqueued[0].CheckId);
    }

    [TestMethod]
    public async Task Coordinator_DoesNotEnqueueBeforeThreshold()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true, thresholdMin: 5);
        var (coord, queue, _) = Coord(db);

        await coord.EvaluateAsync(DownCheck(1, downMinutes: 1), FailType.Unreachable, wasDown: true);

        Assert.AreEqual(0, queue.Enqueued.Count, "below the threshold → no draft");
    }

    [TestMethod]
    public async Task Coordinator_NoEnqueue_WhenAiDisabled_OrOptedOut()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: false, thresholdMin: 5);
        var (coord, queue, _) = Coord(db);

        await coord.EvaluateAsync(DownCheck(1, 10, optIn: true), FailType.Unreachable, wasDown: true);
        Assert.AreEqual(0, queue.Enqueued.Count, "AI master switch off → no draft");

        // AI on but the check opted out.
        db.SiteSettingsSet.Single().AiEnabled = true; await db.SaveChangesAsync();
        await coord.EvaluateAsync(DownCheck(2, 10, optIn: false), FailType.Unreachable, wasDown: true);
        Assert.AreEqual(0, queue.Enqueued.Count, "per-check opt-out → no draft");
    }

    [TestMethod]
    public async Task Coordinator_NoEnqueue_WhenAlreadyOpen()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true, thresholdMin: 5);
        var (coord, queue, incidents) = Coord(db);
        await incidents.CreateAutoIncidentAsync(1, new IncidentDraft("t", "d", IncidentSeverity.Minor, false));

        await coord.EvaluateAsync(DownCheck(1, 10), FailType.Unreachable, wasDown: true);

        Assert.AreEqual(0, queue.Enqueued.Count, "an open auto-incident already exists → no duplicate enqueue");
    }

    [TestMethod]
    public async Task Coordinator_Recovery_ResolvesLinkedAuto_OnlyOnTransition()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true, thresholdMin: 5);
        var (coord, queue, incidents) = Coord(db);
        await incidents.CreateAutoIncidentAsync(1, new IncidentDraft("t", "d", IncidentSeverity.Minor, false));

        // Healthy tick that was NOT preceded by downtime → no resolve attempt.
        await coord.EvaluateAsync(DownCheck(1, 0), FailType.NoFail, wasDown: false);
        Assert.IsTrue(await incidents.HasOpenLinkedAutoIncidentAsync(1), "no transition → still open");

        // Healthy tick on the down→healthy edge → resolves the linked auto-incident.
        await coord.EvaluateAsync(DownCheck(1, 0), FailType.NoFail, wasDown: true);
        Assert.IsFalse(await incidents.HasOpenLinkedAutoIncidentAsync(1), "recovery resolves it");
        Assert.AreEqual(0, queue.Enqueued.Count);
    }

    [TestMethod]
    public async Task RunCheckNow_Recovery_ResolvesLinkedAutoIncident()
    {
        // Hermes #4: a manual run-now that recovers a down check must resolve its
        // linked auto-incident — not just clear DownSinceUtc and strand it open.
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        await SeedSettings(db, aiEnabled: true, thresholdMin: 5);

        var check = new StatusCheck
        {
            Title = "API", StatusCheckUrl = "https://api.example.com/health", ServiceLogoUrl = "",
            Enabled = true, AutoIncidentEnabled = true, ExpectedStatusCode = 200,
            IntervalSeconds = 60, ConsecutiveFailures = 3, DownSinceUtc = DateTime.UtcNow.AddMinutes(-10), Created = DateTime.UtcNow,
        };
        SlaTestUtil.Attach(check, slowThresholdMs: 60_000);   // #293: classification reads the linked SLA (generous so timing never trips ResponseTime)
        db.StatusCheckSet.Add(check); await db.SaveChangesAsync();

        var incidents = new IncidentService(new IncidentRepository(db));
        await incidents.CreateAutoIncidentAsync(check.Id, new IncidentDraft("API down", "d", IncidentSeverity.Minor, false));
        Assert.IsTrue(await incidents.HasOpenLinkedAutoIncidentAsync(check.Id));

        var coord = new AutoIncidentCoordinator(new SiteSettingsRepository(db), incidents, new FakeQueue());
        var svc = new StatusCheckService(
            new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)), // healthy probe
            NullLogger<StatusCheckService>.Instance, coord);

        await svc.RunCheckNowAsync(check.Id);

        Assert.IsFalse(await incidents.HasOpenLinkedAutoIncidentAsync(check.Id),
            "manual run-now recovery resolves the linked auto-incident");
    }
}
