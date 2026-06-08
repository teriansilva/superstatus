using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #136 — the dashboard-summary perf slice: the DB-side daily rollup
/// (replacing the in-memory 30-day materialization), the rollup-driven summary
/// strip, and the short-TTL cache contract.
/// </summary>
[TestClass]
public class SummaryPerfTests
{
    private static (SuperStatusDb db, SqliteConnection conn) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck Check(SuperStatusDb db, int expectedStatus = 200, long expectedRt = 1000)
    {
        var c = new StatusCheck { Title = "svc", StatusCheckUrl = "x", WebHookOnErrorUrl = "", ServiceLogoUrl = "", ExpectedStatusCode = expectedStatus, ExpectedResponseTimeInMs = expectedRt, Enabled = true, IntervalSeconds = 30, Created = DateTime.UtcNow };
        db.StatusCheckSet.Add(c); db.SaveChanges(); return c;
    }

    private static void Tick(SuperStatusDb db, long checkId, DateTime utc, int code = 200, long ms = 100, bool failed = false)
        => db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = checkId, TimeOfCheckUTC = utc, HttpStatusCode = code, ResponseTimeInMs = ms, CheckFailed = failed });

    [TestMethod]
    public async Task DailyRollup_CountsPerDay_DownDegradedTotal()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db, expectedStatus: 200, expectedRt: 1000);
        var d1 = new DateTime(2026, 5, 27, 8, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 5, 28, 8, 0, 0, DateTimeKind.Utc);
        // Day 1: 2 healthy, 1 degraded (slow), 1 down (500). Day 2: 3 healthy.
        Tick(db, c.Id, d1, 200, 100); Tick(db, c.Id, d1.AddMinutes(1), 200, 200);
        Tick(db, c.Id, d1.AddMinutes(2), 200, 5000);          // degraded (slow)
        Tick(db, c.Id, d1.AddMinutes(3), 500, 80);            // down (status)
        Tick(db, c.Id, d2, 200, 90); Tick(db, c.Id, d2.AddMinutes(1), 200, 90); Tick(db, c.Id, d2.AddMinutes(2), 200, 90);
        await db.SaveChangesAsync();

        var rollup = await new HistoricalStatusDataRepository(db)
            .GetDailyStateRollupAsync(c.Id, 200, 1000, d1.Date);
        var byDay = rollup.ToDictionary(r => r.Day.Date);

        Assert.AreEqual(4, byDay[d1.Date].Total);
        Assert.AreEqual(1, byDay[d1.Date].Down);
        Assert.AreEqual(1, byDay[d1.Date].Degraded);
        Assert.AreEqual(3, byDay[d2.Date].Total);
        Assert.AreEqual(0, byDay[d2.Date].Down);
        Assert.AreEqual(0, byDay[d2.Date].Degraded);
    }

    [TestMethod]
    public async Task Summary_StripReflectsWorstOfDay_FromRollup()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db);
        var now = DateTime.UtcNow;
        // Today: a healthy tick then a more-recent down tick → today's cell is
        // worst-of-day (down) AND current state is down (latest).
        Tick(db, c.Id, now.AddMinutes(-2), 200, 100);
        Tick(db, c.Id, now.AddMinutes(-1), 500, 100);   // most recent → down
        await db.SaveChangesAsync();

        var svc = new StatusCheckService(
            new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new NoopFactory(), NullLogger<StatusCheckService>.Instance);

        var summary = await svc.GetDashboardSummaryAsync(0);
        var strip = summary.PerService.Single().Uptime30d;
        Assert.AreEqual("down", strip[^1], "today's cell = worst-of-day (down) from the rollup");
        Assert.AreEqual("gap", strip[0], "a day with no ticks = gap, never silently up");
        Assert.AreEqual("down", summary.PerService.Single().CurrentState, "current state = most-recent tick");
    }

    [TestMethod]
    public async Task SummaryCache_HitWithinTtl_DoesNotRecompute()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        int computes = 0;
        Func<Task<DashboardSummaryViewModel>> factory = () =>
        {
            computes++;
            return Task.FromResult(new DashboardSummaryViewModel(
                new DashboardServiceCountsViewModel(0, 0, 0, 0),
                new DashboardLatencyViewModel(null, null),
                0, 0, new List<DashboardPerServiceViewModel>(), "up", DateTime.UtcNow));
        };

        var first = await DashboardCache.GetOrComputeAsync(cache, factory);
        var second = await DashboardCache.GetOrComputeAsync(cache, factory);

        Assert.IsNotNull(first);
        Assert.AreSame(first, second, "second call returns the cached instance");
        Assert.AreEqual(1, computes, "factory runs once within the TTL — a cache hit recomputes nothing");
    }

    [TestMethod]
    public async Task SummaryCache_ConcurrentColdCallers_ComputeExactlyOnce()
    {
        // #136 (Hermes): the cache must be single-flight — a refresh storm of
        // concurrent cold-cache callers must NOT fan out into many computes.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        int computes = 0;
        Func<Task<DashboardSummaryViewModel>> factory = async () =>
        {
            Interlocked.Increment(ref computes);
            await Task.Delay(50);   // hold the in-flight window open so callers overlap
            return new DashboardSummaryViewModel(
                new DashboardServiceCountsViewModel(0, 0, 0, 0),
                new DashboardLatencyViewModel(null, null),
                0, 0, new List<DashboardPerServiceViewModel>(), "up", DateTime.UtcNow);
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => DashboardCache.GetOrComputeAsync(cache, factory)));

        Assert.AreEqual(1, computes, "20 concurrent cold-cache callers must compute exactly once");
        Assert.IsTrue(results.All(r => ReferenceEquals(r, results[0])), "all callers get the same coalesced instance");
    }

    private sealed class NoopFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Noop());
        private sealed class Noop : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
