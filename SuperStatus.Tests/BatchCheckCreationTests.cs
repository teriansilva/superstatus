using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Providers;
using SuperStatus.Services.Providers.Ai;
using SuperStatus.Services.Providers.Heartbeat;
using SuperStatus.Services.Providers.Http;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Epic #342 (batch add) — the transactional create service against a relational
/// (SQLite) DB. Covers the happy path (valid targets → checks sharing SLA / webhook /
/// profile / interval / alert thresholds; dup + invalid lines skipped), the 422
/// rejections (unknown provider, not batch-capable, over-cap, unknown webhook id, zero
/// valid targets), cross-check canonical dedup, and — the key atomicity invariant — a
/// mid-batch failure rolling back every created check AND its link rows.
/// </summary>
[TestClass]
public class BatchCheckCreationTests
{
    private static (SuperStatusDb db, SqliteConnection conn) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static BatchCheckCreationService Build(SuperStatusDb db, ISlaNormalizationService? sla = null, bool includeHeartbeat = false)
    {
        var factory = new StubHttpClientFactory();
        var providers = new List<ICheckProvider> { new HttpCheckProvider(factory), new AiCheckProvider(factory) };
        if (includeHeartbeat) providers.Add(new HeartbeatCheckProvider());
        var registry = new CheckProviderRegistry(providers);

        var statusCheckService = new StatusCheckService(
            new StatusCheckRepository(db),
            new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db),
            new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            factory,
            NullLogger<StatusCheckService>.Instance,
            checkProviderRegistry: registry);

        return new BatchCheckCreationService(
            registry,
            statusCheckService,
            LinkedTargetTestUtil.Normalization(db),
            sla ?? SlaTestUtil.Normalization(db),
            new Repository<Sla>(db),
            db);
    }

    private static Sla SeedDefaultSla(SuperStatusDb db)
    {
        var sla = new Sla { Name = "Default", TargetUptimePercent = 100, CriticalUptimePercent = 100, SlowThresholdMs = 1000, IsDefault = true, CreatedUtc = DateTime.UtcNow };
        db.SlaSet.Add(sla);
        db.SaveChanges();
        return sla;
    }

    private static Webhook SeedWebhook(SuperStatusDb db)
    {
        var w = new Webhook { Name = "Ops", Url = "https://hooks.ex/err", IsEnabled = true, ThrottleMinutes = 0, CreatedUtc = DateTime.UtcNow };
        db.WebhookSet.Add(w);
        db.SaveChanges();
        return w;
    }

    private static AlertProfile SeedProfile(SuperStatusDb db)
    {
        var p = new AlertProfile { Name = "Oncall", EmailEnabled = true, EmailRecipients = "o@x.com", UsesSiteDefaultRecipients = false, WebPushEnabled = false, CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.Add(p);
        db.SaveChanges();
        return p;
    }

    private static BatchCreateChecksRequest Req(IEnumerable<string> targets, Action<BatchCreateChecksRequest>? tweak = null)
    {
        var r = new BatchCreateChecksRequest
        {
            ProviderType = "http",
            Targets = targets.ToList(),
            SharedConfig = new Dictionary<string, string> { ["expectedStatusCode"] = "200" },
            IntervalSeconds = 45,
        };
        tweak?.Invoke(r);
        return r;
    }

    // ---- happy path ------------------------------------------------------------

    [TestMethod]
    public async Task Batch_CreatesValid_SkipsDupAndInvalid_SharingSettings()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var sla = SeedDefaultSla(db);
        var wh = SeedWebhook(db);
        var pr = SeedProfile(db);
        var svc = Build(db);

        var req = Req(new[]
        {
            "https://web.example.com/health",
            "https://api.example.com/healthz",
            "https://web.example.com/health", // duplicate
            "http://",                          // invalid (no host)
        }, r =>
        {
            r.SlaId = sla.Id;
            r.WebhookIds = new List<long> { wh.Id };
            r.AlertProfileIds = new List<long> { pr.Id };
            r.AlertOnFailureThreshold = 3;
            r.AlertOnOutageMinutes = 5;
            r.AlertThrottleMinutes = 15;
            r.Enabled = true;
        });

        var outcome = await svc.CreateBatchAsync(req);

        Assert.IsFalse(outcome.Rejected, outcome.RejectionMessage);
        Assert.AreEqual(2, outcome.Response!.CreatedCount);
        Assert.AreEqual(2, outcome.Response.SkippedCount);

        var checks = db.StatusCheckSet.AsNoTracking().ToList();
        Assert.AreEqual(2, checks.Count);
        Assert.IsTrue(checks.All(c => c.IntervalSeconds == 45 && c.AlertOnFailureThreshold == 3 && c.AlertThrottleMinutes == 15 && c.SlaId == sla.Id && c.Enabled),
            "every created check shares the batch's interval / thresholds / SLA / enabled state");
        CollectionAssert.AreEquivalent(new[] { "web.example.com", "api.example.com" }, checks.Select(c => c.Title).ToList());
        Assert.AreEqual(2, db.StatusCheckWebhookSet.AsNoTracking().Count(), "each check is linked to the shared webhook");
        Assert.AreEqual(2, db.StatusCheckAlertProfileSet.AsNoTracking().Count(), "each check is linked to the shared alert profile");
    }

    // ---- 422 rejections --------------------------------------------------------

    [TestMethod]
    public async Task Batch_UnknownWebhookId_Rejected_NothingWritten()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        SeedDefaultSla(db);
        var svc = Build(db);

        var outcome = await svc.CreateBatchAsync(Req(new[] { "https://a.example.com" }, r => r.WebhookIds = new List<long> { 999 }));

        Assert.IsTrue(outcome.Rejected);
        StringAssert.Contains(outcome.RejectionMessage, "webhook");
        Assert.AreEqual(0, db.StatusCheckSet.AsNoTracking().Count());
    }

    [TestMethod]
    public async Task Batch_OverCap_Rejected()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = Build(db);

        var many = Enumerable.Range(0, BatchCheckCreationService.MaxBatchSize + 1).Select(i => $"https://h{i}.example.com");
        var outcome = await svc.CreateBatchAsync(Req(many));

        Assert.IsTrue(outcome.Rejected);
        StringAssert.Contains(outcome.RejectionMessage, "Too many");
        Assert.AreEqual(0, db.StatusCheckSet.AsNoTracking().Count());
    }

    [TestMethod]
    public async Task Batch_ZeroValidTargets_Rejected()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = Build(db);

        var outcome = await svc.CreateBatchAsync(Req(new[] { "http://", "https://" })); // both have no host

        Assert.IsTrue(outcome.Rejected);
        StringAssert.Contains(outcome.RejectionMessage, "No valid");
        Assert.AreEqual(0, db.StatusCheckSet.AsNoTracking().Count());
    }

    [TestMethod]
    public async Task Batch_UnknownProvider_Rejected()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = Build(db);

        var outcome = await svc.CreateBatchAsync(Req(new[] { "https://a" }, r => r.ProviderType = "nope"));

        Assert.IsTrue(outcome.Rejected);
        StringAssert.Contains(outcome.RejectionMessage, "Unknown provider");
    }

    [TestMethod]
    public async Task Batch_ProviderWithoutTarget_Rejected()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = Build(db, includeHeartbeat: true);

        var outcome = await svc.CreateBatchAsync(Req(new[] { "https://a" }, r => r.ProviderType = "heartbeat"));

        Assert.IsTrue(outcome.Rejected);
        StringAssert.Contains(outcome.RejectionMessage, "does not support");
    }

    // ---- cross-check dedup -----------------------------------------------------

    [TestMethod]
    public async Task Batch_SkipsTargetAlreadyMonitored()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var sla = SeedDefaultSla(db);
        var svc = Build(db);

        await svc.CreateBatchAsync(Req(new[] { "https://web.example.com/health" }, r => r.SlaId = sla.Id));

        // A trailing-slash / scheme-less form of the SAME target must dedup against it.
        var outcome = await svc.CreateBatchAsync(Req(new[] { "web.example.com/health/", "https://fresh.example.com" }, r => r.SlaId = sla.Id));

        Assert.IsFalse(outcome.Rejected, outcome.RejectionMessage);
        Assert.AreEqual(1, outcome.Response!.CreatedCount);
        Assert.AreEqual(1, outcome.Response.SkippedCount);
        Assert.AreEqual(2, db.StatusCheckSet.AsNoTracking().Count());
    }

    // ---- shared-config schema validation (Hermes #346) -------------------------

    [TestMethod]
    public async Task Batch_AiMissingRequiredSharedFields_Rejected_NothingWritten()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        SeedDefaultSla(db);
        var svc = Build(db);

        // A valid baseUrl target, but the required shared fields (model/prompt/expectContains)
        // are blank — this must fail the whole batch, not create enabled-but-invalid checks.
        var req = new BatchCreateChecksRequest
        {
            ProviderType = "ai",
            Targets = new List<string> { "https://api.openai.com/v1" },
            SharedConfig = new Dictionary<string, string>(),
            IntervalSeconds = 60,
        };

        var outcome = await svc.CreateBatchAsync(req);

        Assert.IsTrue(outcome.Rejected);
        StringAssert.Contains(outcome.RejectionMessage, "incomplete");
        Assert.AreEqual(0, db.StatusCheckSet.AsNoTracking().Count(), "no checks created when the shared config is invalid");
    }

    [TestMethod]
    public async Task Batch_AiWithCompleteSharedFields_Creates()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var sla = SeedDefaultSla(db);
        var svc = Build(db);

        var req = new BatchCreateChecksRequest
        {
            ProviderType = "ai",
            Targets = new List<string> { "https://api.openai.com/v1", "https://llm.internal/v1" },
            SharedConfig = new Dictionary<string, string>
            {
                ["model"] = "gpt-4o-mini",
                ["prompt"] = "Reply with the single word: pong",
                ["expectContains"] = "pong",
            },
            SlaId = sla.Id,
            IntervalSeconds = 60,
        };

        var outcome = await svc.CreateBatchAsync(req);

        Assert.IsFalse(outcome.Rejected, outcome.RejectionMessage);
        Assert.AreEqual(2, outcome.Response!.CreatedCount);
        var checks = db.StatusCheckSet.AsNoTracking().ToList();
        Assert.AreEqual(2, checks.Count);
        Assert.IsTrue(checks.All(c => c.ProviderType == "ai"));
    }

    // ---- atomicity -------------------------------------------------------------

    [TestMethod]
    public async Task Batch_MidTransactionFailure_RollsBackChecksAndLinks()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var sla = SeedDefaultSla(db);
        var wh = SeedWebhook(db);
        // ApplyEditSla throws mid-loop, AFTER a check + its webhook link were written.
        var svc = Build(db, sla: new ThrowingSlaService());

        var req = Req(new[] { "https://a.example.com", "https://b.example.com" }, r =>
        {
            r.SlaId = sla.Id;
            r.WebhookIds = new List<long> { wh.Id };
        });

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => svc.CreateBatchAsync(req));

        Assert.AreEqual(0, db.StatusCheckSet.AsNoTracking().Count(), "created checks are rolled back");
        Assert.AreEqual(0, db.StatusCheckWebhookSet.AsNoTracking().Count(), "their webhook links are rolled back too");
    }

    private sealed class ThrowingSlaService : ISlaNormalizationService
    {
        public Task ApplyEditSlaAsync(StatusCheck check, long? requestedSlaId, bool isNewCheck, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated mid-batch failure");
        public Task<SlaBackfillSummary> BackfillAsync(bool dryRun, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetDefaultAsync(long slaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
