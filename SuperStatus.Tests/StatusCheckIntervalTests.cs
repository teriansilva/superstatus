using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Scheduler;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #82 — per-check polling interval, against a relational (SQLite)
/// model: the bulk last-check-times query, the server-side clamp on save, and
/// the scheduler actually skipping not-yet-due checks through the real scoped
/// services (production path, as in #78).
/// </summary>
[TestClass]
public class StatusCheckIntervalTests
{
    private static (SuperStatusDb, SqliteConnection) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck Check(int interval, long id = 0) => new()
    {
        Id = id,
        Title = "probe",
        StatusCheckUrl = "http://probe.test/health",
        ExpectedStatusCode = 200,
        Sla = SlaTestUtil.Mirror(60_000),   // #293: classification reads the linked SLA
        Enabled = true,
        ServiceLogoUrl = string.Empty,
        IntervalSeconds = interval,
        Created = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task GetLastCheckTimes_ReturnsLatestPerCheck_AndOmitsNoHistory()
    {
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var a = Check(60); var b = Check(60); var c = Check(60); // c has no history
        db.StatusCheckSet.AddRange(a, b, c);
        await db.SaveChangesAsync();
        var baseUtc = new DateTime(2026, 5, 29, 12, 0, 0, DateTimeKind.Utc);
        db.HistoricalStatusDataSet.AddRange(
            new HistoricalStatusData { StatusCheckId = a.Id, TimeOfCheckUTC = baseUtc.AddMinutes(-5) },
            new HistoricalStatusData { StatusCheckId = a.Id, TimeOfCheckUTC = baseUtc.AddMinutes(-1) }, // latest for a
            new HistoricalStatusData { StatusCheckId = b.Id, TimeOfCheckUTC = baseUtc.AddMinutes(-3) });
        await db.SaveChangesAsync();
        var repo = new HistoricalStatusDataRepository(db);

        var map = await repo.GetLastCheckTimesAsync(new[] { a.Id, b.Id, c.Id });

        Assert.AreEqual(baseUtc.AddMinutes(-1), map[a.Id], "Must be the most-recent tick for a.");
        Assert.AreEqual(baseUtc.AddMinutes(-3), map[b.Id]);
        Assert.IsFalse(map.ContainsKey(c.Id), "A check with no history must be absent (treated as never-run).");
    }

    [DataTestMethod]
    [DataRow(1, 30)]        // below min → clamped up to the 30 s floor (#136)
    [DataRow(99_999, 3600)] // above max → clamped down
    [DataRow(120, 120)]     // within range → unchanged
    public async Task AddOrUpdate_ClampsIntervalServerSide(int input, int expected)
    {
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var svc = Svc(db, _ => new HttpResponseMessage(HttpStatusCode.OK));

        var saved = await svc.AddOrUpdateStatusCheck(new StatusCheckViewModelBase
        {
            Title = "x", StatusCheckUrl = "http://x", ServiceLogoUrl = "",
            ExpectedStatusCode = 200, Enabled = true, IntervalSeconds = input,
        });

        Assert.AreEqual(expected, saved.IntervalSeconds);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AddOrUpdate_PersistsAutoIncidentEnabled_OnCreate(bool optIn)
    {
        // #168: enabling the per-check "auto-draft an incident" toggle while
        // CREATING a check must stick (regression: the create path dropped it).
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var svc = Svc(db, _ => new HttpResponseMessage(HttpStatusCode.OK));

        var saved = await svc.AddOrUpdateStatusCheck(new StatusCheckViewModelBase
        {
            Title = "x", StatusCheckUrl = "http://x", ServiceLogoUrl = "",
            ExpectedStatusCode = 200, Enabled = true, IntervalSeconds = 60, AutoIncidentEnabled = optIn,
        });

        Assert.AreEqual(optIn, saved.AutoIncidentEnabled, "create path persists the opt-in");
        var reread = await db.StatusCheckSet.AsNoTracking().SingleAsync(c => c.Id == saved.Id);
        Assert.AreEqual(optIn, reread.AutoIncidentEnabled, "persisted to the DB");
    }

    [TestMethod]
    public async Task Scheduler_SkipsNotYetDue_AndRunsDue()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<SuperStatusDb>(o => o.UseSqlite(conn), ServiceLifetime.Scoped);
            services.AddRepositories();
            services.AddScoped<IStatusCheckService, StatusCheckService>();
            services.AddSingleton<IHttpClientFactory>(new StubFactory());
            services.AddSingleton(new SchedulerConcurrencyOptions(4));
            await using var sp = services.BuildServiceProvider();

            long notDueId, dueId;
            await using (var s = sp.CreateAsyncScope())
            {
                var db = s.ServiceProvider.GetRequiredService<SuperStatusDb>();
                db.Database.EnsureCreated();
                // "notDue": interval 3600 with a tick 1 minute ago → not due.
                var notDue = Check(3600); db.StatusCheckSet.Add(notDue);
                // "due": interval 5, no history → first run, due.
                var due = Check(5); db.StatusCheckSet.Add(due);
                await db.SaveChangesAsync();
                notDueId = notDue.Id; dueId = due.Id;
                db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = notDueId, TimeOfCheckUTC = DateTime.UtcNow.AddMinutes(-1) });
                await db.SaveChangesAsync();
            }

            await new SuperStatusCheckJob(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<SchedulerConcurrencyOptions>(),
                NullLogger<StatusCheckService>.Instance).RunDueChecksAsync();

            await using var verify = sp.CreateAsyncScope();
            var vdb = verify.ServiceProvider.GetRequiredService<SuperStatusDb>();
            Assert.AreEqual(1, await vdb.HistoricalStatusDataSet.CountAsync(h => h.StatusCheckId == notDueId),
                "Not-yet-due check must keep only its seeded tick (no new run).");
            Assert.AreEqual(1, await vdb.HistoricalStatusDataSet.CountAsync(h => h.StatusCheckId == dueId),
                "Due (never-run) check must have executed exactly once.");
        }
        finally { conn.Close(); }
    }

    private static StatusCheckService Svc(SuperStatusDb db, Func<HttpRequestMessage, HttpResponseMessage> r) =>
        new(new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new StubFactory(r), NullLogger<StatusCheckService>.Instance);

    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _r = responder ?? (_ => new HttpResponseMessage(HttpStatusCode.OK));
        public HttpClient CreateClient(string name) => new(new H(_r));
        private sealed class H(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            { await Task.Yield(); return f(request); }
        }
    }
}
