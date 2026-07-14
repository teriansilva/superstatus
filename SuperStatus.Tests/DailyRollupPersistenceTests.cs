using System.Net;
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
/// Issue #138 (P1 PR-A): the persisted daily rollup — upsert idempotency, the
/// refresh job, and proof the dashboard summary reads PAST days from the
/// rollup table (not by re-aggregating raw), with TODAY computed live.
/// </summary>
[TestClass]
public class DailyRollupPersistenceTests
{
    private static (SuperStatusDb db, SqliteConnection conn) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck Check(SuperStatusDb db)
    {
        var c = new StatusCheck { Title = "svc", StatusCheckUrl = "x", ServiceLogoUrl = "", ExpectedStatusCode = 200, Enabled = true, IntervalSeconds = 30, Created = DateTime.UtcNow };
        SlaTestUtil.Attach(c);   // #293: classification reads the linked SLA
        db.StatusCheckSet.Add(c); db.SaveChanges(); return c;
    }

    private static StatusCheckService Svc(SuperStatusDb db) =>
        new(new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db), new NoopFactory(), NullLogger<StatusCheckService>.Instance);

    [TestMethod]
    public async Task Upsert_IsIdempotent_UpdatesNotDuplicates()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db);
        var repo = new DailyStatusRollupRepository(db);
        var day = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);

        await repo.UpsertAsync(c.Id, day, total: 100, down: 2, degraded: 1);
        await repo.UpsertAsync(c.Id, day, total: 120, down: 5, degraded: 3);   // re-run, new counts

        var rows = await repo.GetSinceAsync(c.Id, day);
        Assert.AreEqual(1, rows.Count, "one row per (check, day) — re-run updates, never duplicates");
        Assert.AreEqual(120, rows[0].Total);
        Assert.AreEqual(5, rows[0].Down);
        Assert.AreEqual(3, rows[0].Degraded);
    }

    [TestMethod]
    public async Task Refresh_PersistsPerDayRollupsFromRaw()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db);
        var d1 = DateTime.UtcNow.Date.AddDays(-2).AddHours(8);
        db.HistoricalStatusDataSet.AddRange(
            new HistoricalStatusData { StatusCheckId = c.Id, TimeOfCheckUTC = d1, HttpStatusCode = 200, ResponseTimeInMs = 100, FailType = FailType.NoFail },
            new HistoricalStatusData { StatusCheckId = c.Id, TimeOfCheckUTC = d1.AddMinutes(1), HttpStatusCode = 500, ResponseTimeInMs = 100, FailType = FailType.StatusCode }); // down
        await db.SaveChangesAsync();

        await Svc(db).RefreshDailyRollupsAsync(7);

        var rows = await new DailyStatusRollupRepository(db).GetSinceAsync(c.Id, d1.Date);
        var day = rows.Single(r => r.Day.Date == d1.Date);
        Assert.AreEqual(2, day.Total);
        Assert.AreEqual(1, day.Down);
    }

    [TestMethod]
    public async Task Summary_ReadsPastDayFromPersistedRollup_NotRaw()
    {
        // The decisive test: seed a persisted rollup for a PAST day with NO raw
        // ticks for it. If the summary shows that day "down", it read the
        // rollup table — not by re-aggregating raw (which has nothing).
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var c = Check(db);
        var pastDay = DateTime.UtcNow.Date.AddDays(-5);
        await new DailyStatusRollupRepository(db).UpsertAsync(c.Id, pastDay, total: 50, down: 3, degraded: 0);
        // No raw ticks at all.
        await db.SaveChangesAsync();

        var summary = await Svc(db).GetDashboardSummaryAsync(0);
        var strip = summary.PerService.Single().Uptime30d;
        // pastDay is index 24 in the 30-cell window (now-29d .. now); compute it.
        int idx = (pastDay - DateTime.UtcNow.AddDays(-29).Date).Days;
        Assert.AreEqual("down", strip[idx], "past day came from the persisted rollup (no raw existed)");
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
