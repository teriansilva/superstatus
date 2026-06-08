using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #107. Verifies repo queries (newest-first, per-check scope,
/// retention bulk delete) on a SQLite-backed model + the index contract
/// on the runtime model (pattern from #79).
/// </summary>
[TestClass]
public class WebhookExecutionLogTests
{
    private (SuperStatusDb db, SqliteConnection conn) BuildRelationalContext()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<SuperStatusDb>()
            .UseSqlite(conn)
            .Options;
        var db = new SuperStatusDb(options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck NewCheck(long id) =>
        new() { Id = id, Title = $"check-{id}", StatusCheckUrl = "x", WebHookOnErrorUrl = "y", ServiceLogoUrl = "z" };

    [TestMethod]
    public async Task GetRecentForStatusCheck_NewestFirst_BoundedByCount()
    {
        var (db, conn) = BuildRelationalContext();
        using var _ = db;
        using var __ = conn;
        db.StatusCheckSet.AddRange(NewCheck(1), NewCheck(2));
        var baseUtc = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 10; i++)
        {
            db.WebhookExecutionLogSet.Add(new WebhookExecutionLog
            {
                StatusCheckId = 1,
                AttemptedUtc = baseUtc.AddMinutes(i),
                TargetUrl = "https://hook.example/1",
                HttpStatusCode = 200,
                ResponseTimeMs = 40,
                Outcome = WebhookOutcome.Success,
            });
        }
        // Noise from a different check.
        for (int i = 0; i < 3; i++)
        {
            db.WebhookExecutionLogSet.Add(new WebhookExecutionLog
            {
                StatusCheckId = 2,
                AttemptedUtc = baseUtc.AddMinutes(i),
                TargetUrl = "https://hook.example/2",
                HttpStatusCode = 200,
                ResponseTimeMs = 60,
                Outcome = WebhookOutcome.Success,
            });
        }
        await db.SaveChangesAsync();
        var repo = new WebhookExecutionLogRepository(db);

        var rows = await repo.GetRecentForStatusCheckAsync(1, 5);
        Assert.AreEqual(5, rows.Count);
        Assert.IsTrue(rows.All(r => r.StatusCheckId == 1));
        for (int i = 0; i < rows.Count - 1; i++)
        {
            Assert.IsTrue(rows[i].AttemptedUtc > rows[i + 1].AttemptedUtc,
                "Newest-first.");
        }
    }

    [TestMethod]
    public async Task BulkDeleteOlderThanXDaysAsync_RemovesOnlyExpiredRows()
    {
        var (db, conn) = BuildRelationalContext();
        using var _ = db;
        using var __ = conn;
        db.StatusCheckSet.Add(NewCheck(1));
        var now = DateTime.UtcNow;
        db.WebhookExecutionLogSet.AddRange(
            new WebhookExecutionLog { StatusCheckId = 1, AttemptedUtc = now.AddDays(-31), TargetUrl = "x", Outcome = WebhookOutcome.Success },
            new WebhookExecutionLog { StatusCheckId = 1, AttemptedUtc = now.AddDays(-29), TargetUrl = "x", Outcome = WebhookOutcome.Success },
            new WebhookExecutionLog { StatusCheckId = 1, AttemptedUtc = now.AddDays(-1),  TargetUrl = "x", Outcome = WebhookOutcome.Success }
        );
        await db.SaveChangesAsync();
        var repo = new WebhookExecutionLogRepository(db);

        int deleted = await repo.BulkDeleteOlderThanXDaysAsync(30);

        Assert.AreEqual(1, deleted);
        Assert.AreEqual(2, await db.WebhookExecutionLogSet.CountAsync());
        Assert.IsFalse(await db.WebhookExecutionLogSet.AnyAsync(r => r.AttemptedUtc < now.AddDays(-30)));
    }

    [TestMethod]
    public void Model_HasThreeRequiredIndexes()
    {
        // Index contract (#79 pattern): pin the three indexes the
        // service-detail / admin / failures-filter queries depend on.
        var options = new DbContextOptionsBuilder<SuperStatusDb>()
            .UseInMemoryDatabase($"db-{Guid.NewGuid()}").Options;
        using var db = new SuperStatusDb(options);
        var entity = db.Model.FindEntityType(typeof(WebhookExecutionLog));
        Assert.IsNotNull(entity);
        var indexes = entity.GetIndexes().Select(i => i.Properties.Select(p => p.Name).ToArray()).ToList();
        Assert.IsTrue(indexes.Any(i => i.SequenceEqual(new[] { nameof(WebhookExecutionLog.StatusCheckId), nameof(WebhookExecutionLog.AttemptedUtc) })),
            "Composite (StatusCheckId, AttemptedUtc) index is required for service-detail audit reads.");
        Assert.IsTrue(indexes.Any(i => i.SequenceEqual(new[] { nameof(WebhookExecutionLog.AttemptedUtc) })),
            "Single-column AttemptedUtc index is required for the admin global feed + retention cleanup.");
        Assert.IsTrue(indexes.Any(i => i.SequenceEqual(new[] { nameof(WebhookExecutionLog.Outcome) })),
            "Outcome index is required for the failures-only filter chip.");
    }

    [TestMethod]
    public void Constants_OutcomeEnumIsStable()
    {
        // Sentinel — these integer values are persisted to the DB.
        // Changing them silently breaks every existing audit row.
        Assert.AreEqual(0, (int)WebhookOutcome.Success);
        Assert.AreEqual(1, (int)WebhookOutcome.NonSuccess);
        Assert.AreEqual(2, (int)WebhookOutcome.Timeout);
        Assert.AreEqual(3, (int)WebhookOutcome.TransportFailure);
        Assert.AreEqual(4, (int)WebhookOutcome.Skipped);
    }
}
