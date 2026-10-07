using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #201 — the lazy per-day detail used by the uptime-strip hover popover.
/// Verifies the today on-the-fly aggregation (counts/status/uptime), the no-sample
/// "gap" case, and the unknown-check (404) path.
/// </summary>
[TestClass]
public class StatusCheckDayDetailTests
{
    private sealed class NoopFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static (StatusCheckService svc, SuperStatusDb db, SqliteConnection conn) Build()
    {
        var conn = new SqliteConnection("Filename=:memory:"); conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var svc = new StatusCheckService(
            new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db), new NoopFactory(), NullLogger<StatusCheckService>.Instance);
        return (svc, db, conn);
    }

    private static StatusCheck Check() => new()
    {
        Title = "API", StatusCheckUrl = "https://api/health", ServiceLogoUrl = "",
        Enabled = true, ExpectedStatusCode = 200, IntervalSeconds = 60,
        Sla = SlaTestUtil.Mirror(1000),   // #293: the day aggregation reads the linked SLA
        Created = DateTime.UtcNow,
    };

    private static HistoricalStatusData Tick(long checkId, DateTime when, bool failed = false, int code = 200, long ms = 50) => new()
    {
        StatusCheckId = checkId, TimeOfCheckUTC = when, CheckFailed = failed, HttpStatusCode = code, ResponseTimeInMs = ms,
        // #418: rollup/detail classify from FailType, not the HTTP status.
        FailType = failed ? FailType.Unreachable : (code >= 200 && code < 400 ? FailType.NoFail : FailType.StatusCode),
    };

    [TestMethod]
    public async Task UnknownCheck_ReturnsNull()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var detail = await svc.GetDayDetailAsync(999, DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.IsNull(detail, "unknown check → null (endpoint maps to 404)");
    }

    [TestMethod]
    public async Task NoSamples_ReturnsGap()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = Check(); db.StatusCheckSet.Add(check); await db.SaveChangesAsync();

        var detail = await svc.GetDayDetailAsync(check.Id, DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.IsNotNull(detail);
        Assert.AreEqual("gap", detail!.Status);
        Assert.AreEqual(0, detail.Total);
        Assert.AreEqual(0, detail.UptimePct);
    }

    [TestMethod]
    public async Task TodayWithData_ReturnsCountsStatusUptime()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = Check(); db.StatusCheckSet.Add(check); await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        // 8 healthy, 1 slow (200 but over the 1000ms threshold), 1 down (500).
        for (int i = 0; i < 8; i++) db.HistoricalStatusDataSet.Add(Tick(check.Id, now.AddMinutes(-i), code: 200, ms: 50));
        db.HistoricalStatusDataSet.Add(Tick(check.Id, now.AddMinutes(-8), code: 200, ms: 5000)); // slow → degraded
        db.HistoricalStatusDataSet.Add(Tick(check.Id, now.AddMinutes(-9), code: 500, ms: 50));    // bad status → down
        await db.SaveChangesAsync();

        var detail = await svc.GetDayDetailAsync(check.Id, DateOnly.FromDateTime(now));

        Assert.IsNotNull(detail);
        Assert.AreEqual(10, detail!.Total);
        Assert.AreEqual(1, detail.Down);
        Assert.AreEqual(1, detail.Degraded);
        Assert.AreEqual(8, detail.Up);
        Assert.AreEqual("down", detail.Status, "any down sample → the day's worst state is down");
        Assert.AreEqual(80.0, detail.UptimePct, "8 of 10 up");
    }
}
