using System.Net;
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
/// Issue #138 (PR-B): the single canonical read boundary + the batched,
/// rollup-aware summary/grid reads. Proves (a) old days are served from the
/// persisted rollup and recent days from raw, with the two sources agreeing on
/// the same day (equivalence across the cutoff); (b) the batched summary stays
/// correct per check; (c) the grid derives uptime from the rollup-aware read and
/// trailing failures from the persisted #83 counter — not a raw scan.
/// </summary>
[TestClass]
public class DashboardReadBoundaryTests
{
    private static (SuperStatusDb db, SqliteConnection conn) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck Check(SuperStatusDb db, int expectedStatus = 200, long expectedRt = 1000, int consecutiveFailures = 0)
    {
        var c = new StatusCheck
        {
            Title = "svc", StatusCheckUrl = "x", WebHookOnErrorUrl = "", ServiceLogoUrl = "",
            ExpectedStatusCode = expectedStatus, ExpectedResponseTimeInMs = expectedRt,
            Enabled = true, IntervalSeconds = 30, ConsecutiveFailures = consecutiveFailures, Created = DateTime.UtcNow.AddDays(-40),
        };
        db.StatusCheckSet.Add(c); db.SaveChanges(); return c;
    }

    // FailType stored consistently with the thresholds, so the new rollup-recompute
    // (down = fail||code-mismatch, degraded = slow) equals the old FailType==NoFail count.
    private static void Tick(SuperStatusDb db, long checkId, DateTime utc, int code = 200, long ms = 100, bool failed = false)
    {
        FailType ft = failed ? FailType.Unreachable
            : code != 200 ? FailType.StatusCode
            : ms > 1000 ? FailType.ResponseTime
            : FailType.NoFail;
        db.HistoricalStatusDataSet.Add(new HistoricalStatusData
        {
            StatusCheckId = checkId, TimeOfCheckUTC = utc, HttpStatusCode = code,
            ResponseTimeInMs = ms, CheckFailed = failed, FailType = ft,
        });
    }

    private static StatusCheckService Svc(SuperStatusDb db) =>
        new(new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db), new NoopFactory(), NullLogger<StatusCheckService>.Instance);

    [TestMethod]
    public async Task Summary_YesterdayReadFromRollup_NotRaw()
    {
        // #141 follow-up: the read boundary is TODAY — only today is aggregated
        // on-the-fly; every prior day (incl. yesterday) is served from the rollup.
        // Seed yesterday's raw as all-healthy but its rollup as "down": the strip
        // must show the ROLLUP value, proving yesterday is no longer re-scanned.
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db);
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);
        Tick(db, c.Id, yesterday.AddHours(9), 200, 100);   // raw says healthy
        Tick(db, c.Id, yesterday.AddHours(10), 200, 100);
        await new DailyStatusRollupRepository(db).UpsertAsync(c.Id, yesterday, total: 20, down: 5, degraded: 0); // rollup says down
        await db.SaveChangesAsync();

        var summary = await Svc(db).GetDashboardSummaryAsync(0);
        var strip = summary.PerService.Single().Uptime30d;
        int idx = (yesterday - DateTime.UtcNow.AddDays(-29).Date).Days;
        Assert.AreEqual("down", strip[idx], "yesterday is served from the rollup (today-only on-the-fly boundary)");
    }

    [TestMethod]
    public async Task ReadBoundary_ClosedDayRollupEqualsOnTheFlyRecompute()
    {
        // Source-equivalence: for a closed day, the on-the-fly recompute equals
        // the persisted rollup (Total/Down/Degraded), so routing that day to
        // either source yields the same cell.
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db, expectedStatus: 200, expectedRt: 1000);
        var day = DateTime.UtcNow.Date.AddDays(-1).AddHours(9);   // yesterday, still within 72h raw window
        Tick(db, c.Id, day, 200, 100);
        Tick(db, c.Id, day.AddMinutes(1), 200, 5000);   // degraded (slow)
        Tick(db, c.Id, day.AddMinutes(2), 500, 80);     // down (status)
        await db.SaveChangesAsync();

        var batched = (await new HistoricalStatusDataRepository(db)
            .GetRecentDailyStateForChecksAsync(new[] { c.Id }, day.Date))
            .Single(r => r.Day.Date == day.Date);

        await Svc(db).RefreshDailyRollupsAsync(3);
        var persisted = (await new DailyStatusRollupRepository(db).GetSinceAsync(c.Id, day.Date))
            .Single(r => r.Day.Date == day.Date);

        Assert.AreEqual(3, batched.Total);
        Assert.AreEqual(1, batched.Down);
        Assert.AreEqual(1, batched.Degraded);
        Assert.AreEqual(batched.Total, persisted.Total, "rollup Total == on-the-fly Total for the same day");
        Assert.AreEqual(batched.Down, persisted.Down, "rollup Down == on-the-fly Down for the same day");
        Assert.AreEqual(batched.Degraded, persisted.Degraded, "rollup Degraded == on-the-fly Degraded for the same day");
    }

    [TestMethod]
    public async Task Summary_OldDayPrefersRollupOverRaw()
    {
        // A day OLDER than the raw window has BOTH stale raw and a persisted
        // rollup with different counts. The strip must reflect the ROLLUP — that
        // is the read boundary (old days never re-aggregate raw).
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db);
        var oldDay = DateTime.UtcNow.Date.AddDays(-10);   // < firstFullRawDay (≈ now-2d at 72h)
        // Raw for that old day says "all healthy"...
        Tick(db, c.Id, oldDay.AddHours(3), 200, 100);
        Tick(db, c.Id, oldDay.AddHours(4), 200, 100);
        // ...but the persisted rollup (authoritative for old days) says "down".
        await new DailyStatusRollupRepository(db).UpsertAsync(c.Id, oldDay, total: 50, down: 4, degraded: 0);
        await db.SaveChangesAsync();

        var summary = await Svc(db).GetDashboardSummaryAsync(0);
        var strip = summary.PerService.Single().Uptime30d;
        int idx = (oldDay - DateTime.UtcNow.AddDays(-29).Date).Days;
        Assert.AreEqual("down", strip[idx], "old day is served from the rollup, not re-aggregated from raw");
    }

    [TestMethod]
    public async Task Summary_Batched_TwoChecks_EachStripAndStateCorrect()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var a = Check(db);
        var b = Check(db);
        var now = DateTime.UtcNow;
        // Check A: healthy then most-recent down (today).
        Tick(db, a.Id, now.AddMinutes(-3), 200, 100);
        Tick(db, a.Id, now.AddMinutes(-1), 500, 100);   // most recent → down
        // Check B: all healthy today.
        Tick(db, b.Id, now.AddMinutes(-3), 200, 100);
        Tick(db, b.Id, now.AddMinutes(-1), 200, 100);   // most recent → up
        await db.SaveChangesAsync();

        var summary = await Svc(db).GetDashboardSummaryAsync(0);
        var byId = summary.PerService.ToDictionary(s => s.StatusCheckId);

        Assert.AreEqual("down", byId[a.Id].CurrentState);
        Assert.AreEqual("down", byId[a.Id].Uptime30d[^1], "A: today's cell worst-of-day = down");
        Assert.AreEqual("up", byId[b.Id].CurrentState);
        Assert.AreEqual("up", byId[b.Id].Uptime30d[^1], "B: today's cell = up");
        Assert.AreEqual(1, summary.Services.Down);
        Assert.AreEqual(2, summary.Services.Total);
    }

    [TestMethod]
    public async Task Grid_UptimeFromRollupAware_EqualsSampleNoFailFraction()
    {
        // For data inside a single day, grid uptime through the rollup-aware
        // helper equals count(NoFail)/count(all) — the sample-level meaning is
        // unchanged; only the WINDOW edge moved to calendar-day alignment
        // (covered by Grid_Uptime30dWindow_IsCalendarDayAligned).
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db, expectedStatus: 200, expectedRt: 1000, consecutiveFailures: 4);
        var now = DateTime.UtcNow;
        // 10 ticks today: 7 healthy, 2 down (500), 1 degraded (slow) → up = 7/10.
        for (int i = 0; i < 7; i++) Tick(db, c.Id, now.AddMinutes(-30 + i), 200, 100);
        Tick(db, c.Id, now.AddMinutes(-22), 500, 80);
        Tick(db, c.Id, now.AddMinutes(-21), 500, 80);
        Tick(db, c.Id, now.AddMinutes(-20), 200, 5000);   // degraded; also most-recent
        await db.SaveChangesAsync();

        var building = (await Svc(db).GetGridBuildings()).Single(g => g.Id == c.Id);

        Assert.AreEqual(0.7, building.Uptime30d, 1e-9, "up=7 of 10 samples");
        Assert.AreEqual(0.7, building.Uptime7d, 1e-9);
        Assert.AreEqual((int)FailType.ResponseTime, building.CurrentFailType, "most-recent tick was the slow one");
        Assert.AreEqual(4, building.ConsecutiveFailures, "trailing failures come from the persisted #83 counter, not a raw scan");
        Assert.IsNotNull(building.LastCheckUtc);
    }

    [TestMethod]
    public async Task Grid_Uptime30dWindow_IsCalendarDayAligned()
    {
        // Pins the contract change Hermes flagged on #141: the grid window is 30
        // calendar days incl. today (start = now.AddDays(-29).Date), agreeing with
        // the summary strip — NOT a rolling now-30d cutoff. The first in-window day
        // counts; a day just outside does not. Old days are read from the rollup
        // table, so this also exercises the grid's rollup read path.
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db);
        var now = DateTime.UtcNow;
        var rollupRepo = new DailyStatusRollupRepository(db);

        DateTime firstInWindowDay = now.AddDays(-29).Date;   // inclusive lower edge
        DateTime justOutsideDay = now.AddDays(-35).Date;     // must be excluded
        // First in-window day: 10 samples, all down (0 up).
        await rollupRepo.UpsertAsync(c.Id, firstInWindowDay, total: 10, down: 10, degraded: 0);
        // Just-outside day: 10 samples, all down — must NOT affect the 30d figure.
        await rollupRepo.UpsertAsync(c.Id, justOutsideDay, total: 10, down: 10, degraded: 0);
        // Today: 10 healthy raw ticks (10 up of 10).
        for (int i = 0; i < 10; i++) Tick(db, c.Id, now.AddMinutes(-30 + i), 200, 100);
        await db.SaveChangesAsync();

        var building = (await Svc(db).GetGridBuildings()).Single(g => g.Id == c.Id);

        // In-window: 10 down (firstInWindowDay) + 10 up (today) = 20 samples, 10 up
        // → 0.5. If the just-outside day leaked in it would be 10/30 ≈ 0.333.
        Assert.AreEqual(0.5, building.Uptime30d, 1e-9, "30d window = 30 calendar days incl. today; the now-35d day is excluded");
        Assert.AreEqual(1.0, building.Uptime7d, 1e-9, "7d window sees only today's healthy ticks");
    }

    [TestMethod]
    public async Task Grid_NoHistory_RendersHealthy()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db);
        var building = (await Svc(db).GetGridBuildings()).Single(g => g.Id == c.Id);
        Assert.AreEqual(1.0, building.Uptime30d, "a brand-new check with no ticks renders pristine");
        Assert.AreEqual(1.0, building.Uptime7d);
        Assert.IsNull(building.LastCheckUtc);
    }

    [TestMethod]
    public async Task Overview_OldDayFromRollup_PreservesFailureTypeSplit_TodayFromRaw()
    {
        // #138 (PR-C1): the detail/grid-modal overview reads through the canonical
        // boundary too. An OLD day comes from the rollup, keeping the
        // unreachable / bad-status / slow split (bad-status = Down − Unreachable);
        // TODAY is recomputed live from raw.
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db, expectedStatus: 200, expectedRt: 1000);
        var oldDay = DateTime.UtcNow.Date.AddDays(-10);
        // Rollup: 20 total, 5 down (3 unreachable + 2 bad-status), 2 degraded.
        await new DailyStatusRollupRepository(db).UpsertAsync(c.Id, oldDay, total: 20, down: 5, degraded: 2, unreachable: 3);
        // Today raw: 1 unreachable, 1 bad-status, 1 slow, 2 healthy.
        var now = DateTime.UtcNow;
        Tick(db, c.Id, now.AddMinutes(-5), 200, 100, failed: true);   // unreachable
        Tick(db, c.Id, now.AddMinutes(-4), 500, 80);                  // bad status
        Tick(db, c.Id, now.AddMinutes(-3), 200, 5000);               // slow
        Tick(db, c.Id, now.AddMinutes(-2), 200, 100);
        Tick(db, c.Id, now.AddMinutes(-1), 200, 100);
        await db.SaveChangesAsync();

        var overview = (await Svc(db).GetHistoricalStatusDataOverviewForRecentTimeRange(c.Id, 30))
            .ToDictionary(v => v.Date);

        var old = overview[DateOnly.FromDateTime(oldDay)];
        Assert.AreEqual(3, old.UnreachableCount, "old day unreachable from rollup");
        Assert.AreEqual(2, old.FailedResponseCount, "old day bad-status = Down − Unreachable");
        Assert.AreEqual(2, old.SlowResponseCount, "old day slow from rollup Degraded");

        var today = overview[DateOnly.FromDateTime(now.Date)];
        Assert.AreEqual(1, today.UnreachableCount, "today unreachable from raw");
        Assert.AreEqual(1, today.FailedResponseCount, "today bad-status from raw");
        Assert.AreEqual(1, today.SlowResponseCount, "today slow from raw");
    }

    [TestMethod]
    public async Task OverviewForAllChecks_ReturnsPaddedPerCheck_InOneCall()
    {
        // #226: the whole dashboard's strips come back in ONE batched read — every
        // check present, each padded to 30 days — replacing the per-card N+1.
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var a = Check(db);
        var b = Check(db);
        var c = Check(db);   // no history at all
        var oldDay = DateTime.UtcNow.Date.AddDays(-10);
        var rollups = new DailyStatusRollupRepository(db);
        await rollups.UpsertAsync(a.Id, oldDay, total: 10, down: 4, degraded: 0, unreachable: 0); // down day
        await rollups.UpsertAsync(b.Id, oldDay, total: 10, down: 0, degraded: 3, unreachable: 0); // degraded day
        await db.SaveChangesAsync();

        var all = (await Svc(db).GetHistoricalStatusDataOverviewForAllChecks(30))
            .GroupBy(v => v.StatusCheckId)
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Date).ToList());

        CollectionAssert.AreEquivalent(new[] { a.Id, b.Id, c.Id }, all.Keys.ToList());
        Assert.AreEqual(30, all[a.Id].Count, "padded to 30 days");
        Assert.AreEqual(30, all[b.Id].Count);
        Assert.AreEqual(30, all[c.Id].Count);

        Assert.IsTrue(all[a.Id].Single(v => v.Date == DateOnly.FromDateTime(oldDay)).FailedStatus, "a old day down");
        Assert.IsTrue(all[b.Id].Single(v => v.Date == DateOnly.FromDateTime(oldDay)).SlowResponse, "b old day degraded");
        Assert.IsTrue(all[c.Id].All(v => !v.HasData), "c has no samples → all gap");
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
