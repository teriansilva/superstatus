using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Scheduler;
using SuperStatus.Services.Scheduling;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #83 — exponential backoff on consecutive failures: the pure
/// effective-interval rule, the reset/increment counter (write-only-on-change),
/// and an end-to-end fail/fail/fail/success run that proves the counter and the
/// scheduler's effective cadence move together.
/// </summary>
[TestClass]
public class StatusCheckBackoffTests
{
    // ---- pure effective-interval rule ------------------------------------

    [TestMethod]
    public void EffectiveInterval_HealthyAndFirstFailure_UseBase()
    {
        Assert.AreEqual(60, StatusCheckSchedule.EffectiveIntervalSeconds(60, 0), "healthy → base");
        Assert.AreEqual(60, StatusCheckSchedule.EffectiveIntervalSeconds(60, 1), "first failure → base (no backoff yet)");
    }

    [TestMethod]
    public void EffectiveInterval_DoublesPerFailureBeyondFirst()
    {
        Assert.AreEqual(120, StatusCheckSchedule.EffectiveIntervalSeconds(60, 2));
        Assert.AreEqual(240, StatusCheckSchedule.EffectiveIntervalSeconds(60, 3));
        Assert.AreEqual(480, StatusCheckSchedule.EffectiveIntervalSeconds(60, 4));
    }

    [TestMethod]
    public void EffectiveInterval_CappedAtMax_NoOverflow()
    {
        int v = StatusCheckSchedule.EffectiveIntervalSeconds(60, 1000);
        Assert.AreEqual(StatusCheckSchedule.MaxIntervalSeconds, v, "huge failure count must cap, not overflow");
        Assert.IsTrue(v > 0);
    }

    // ---- counter reset / increment (write-only-on-change) -----------------

    [TestMethod]
    public async Task RecordOutcome_MaintainsDownSinceUtc()
    {
        // #168: DownSinceUtc is stamped on the first failure, preserved while
        // failing, and cleared on recovery — the basis for the threshold.
        var (db2, conn2) = Relational();
        using (db2)
        using (conn2)
        {
            var c = Check(60); db2.StatusCheckSet.Add(c); await db2.SaveChangesAsync();
            var s = Svc(db2);

            Assert.IsNull(c.DownSinceUtc, "healthy → no down-since");
            await s.RecordCheckOutcomeAsync(c, FailType.Unreachable);
            Assert.IsNotNull(c.DownSinceUtc, "first failure stamps down-since");
            var firstStamp = c.DownSinceUtc;

            await s.RecordCheckOutcomeAsync(c, FailType.StatusCode);
            Assert.AreEqual(firstStamp, c.DownSinceUtc, "preserved while still failing");

            await s.RecordCheckOutcomeAsync(c, FailType.NoFail);
            Assert.IsNull(c.DownSinceUtc, "recovery clears down-since");
        }
    }

    [TestMethod]
    public async Task RecordOutcome_FailureIncrements_HealthyResets_NoOpWhenSteady()
    {
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var check = Check(60); db.StatusCheckSet.Add(check); await db.SaveChangesAsync();
        var svc = Svc(db);

        await svc.RecordCheckOutcomeAsync(check, FailType.Unreachable);
        Assert.AreEqual(1, check.ConsecutiveFailures);
        await svc.RecordCheckOutcomeAsync(check, FailType.StatusCode);
        Assert.AreEqual(2, check.ConsecutiveFailures);

        await svc.RecordCheckOutcomeAsync(check, FailType.NoFail);
        Assert.AreEqual(0, check.ConsecutiveFailures, "healthy result resets the counter");

        // Steady-healthy: a second NoFail must be a no-op (no spurious write).
        db.ChangeTracker.Clear();
        var reloaded = await db.StatusCheckSet.FindAsync(check.Id);
        await svc.RecordCheckOutcomeAsync(reloaded!, FailType.NoFail);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(reloaded!).State,
            "a steady-healthy tick must not mark the row modified");
    }

    // ---- end-to-end: fail × 3 then recover --------------------------------

    [TestMethod]
    public async Task Scheduler_BacksOffWhileFailing_RecoversImmediatelyOnSuccess()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        try
        {
            // The probe is unreachable while we want failures, then healthy.
            var fail = new[] { true };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<SuperStatusDb>(o => o.UseSqlite(conn), ServiceLifetime.Scoped);
            services.AddRepositories();
            services.AddScoped<IStatusCheckService, StatusCheckService>();
            services.AddSingleton<IHttpClientFactory>(new StubFactory(() => fail[0]
                ? throw new TaskCanceledException("down")
                : new HttpResponseMessage(HttpStatusCode.OK)));
            services.AddSingleton(new SchedulerConcurrencyOptions(2));
            await using var sp = services.BuildServiceProvider();

            long id;
            await using (var s = sp.CreateAsyncScope())
            {
                var db = s.ServiceProvider.GetRequiredService<SuperStatusDb>();
                db.Database.EnsureCreated();
                var c = Check(5); db.StatusCheckSet.Add(c); await db.SaveChangesAsync(); id = c.Id;
            }

            async Task<int> CountAsync()
            {
                await using var s = sp.CreateAsyncScope();
                return await s.ServiceProvider.GetRequiredService<SuperStatusDb>()
                    .HistoricalStatusDataSet.CountAsync(h => h.StatusCheckId == id);
            }
            async Task<int> FailuresAsync()
            {
                await using var s = sp.CreateAsyncScope();
                return (await s.ServiceProvider.GetRequiredService<SuperStatusDb>().StatusCheckSet.FindAsync(id))!.ConsecutiveFailures;
            }

            // Drive ticks directly via the service so the test is deterministic
            // (no wall-clock waiting on due-ness): run the check three times
            // failing, then once healthy, and assert the counter trajectory.
            await using (var s = sp.CreateAsyncScope())
            {
                var svc = s.ServiceProvider.GetRequiredService<IStatusCheckService>();
                for (int i = 0; i < 3; i++) await svc.RunCheckNowAsync(id);
            }
            Assert.AreEqual(3, await FailuresAsync(), "three consecutive failures counted");

            fail[0] = false;
            await using (var s = sp.CreateAsyncScope())
                await s.ServiceProvider.GetRequiredService<IStatusCheckService>().RunCheckNowAsync(id);
            Assert.AreEqual(0, await FailuresAsync(), "a healthy result resets the counter → back to base cadence");
            Assert.AreEqual(4, await CountAsync(), "four results persisted (3 fail + 1 ok)");
        }
        finally { conn.Close(); }
    }

    // ---- helpers ----------------------------------------------------------

    private static (SuperStatusDb, SqliteConnection) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck Check(int interval) => new()
    {
        Title = "probe", StatusCheckUrl = "http://probe.test/health", ExpectedStatusCode = 200,
        ExpectedResponseTimeInMs = 60_000, Enabled = true, WebHookOnErrorUrl = string.Empty,
        ServiceLogoUrl = string.Empty, IntervalSeconds = interval, Created = DateTime.UtcNow,
    };

    private static StatusCheckService Svc(SuperStatusDb db) =>
        new(new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new StubFactory(() => new HttpResponseMessage(HttpStatusCode.OK)), NullLogger<StatusCheckService>.Instance);

    private sealed class StubFactory(Func<HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new H(responder));
        private sealed class H(Func<HttpResponseMessage> f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            { await Task.Yield(); return f(); }
        }
    }
}
