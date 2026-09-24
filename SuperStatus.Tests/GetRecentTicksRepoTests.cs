using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #103: GetRecentTicks(checkId, count) returns at most `count` rows
/// ordered newest-first, scoped to one StatusCheckId. SQLite-backed because
/// the InMemory provider's ordering on `OrderByDescending` does not always
/// preserve insertion order on equal-key rows.
/// </summary>
[TestClass]
public class GetRecentTicksRepoTests
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

    [TestMethod]
    public async Task ReturnsRowsForOnlyTheRequestedCheckId_NewestFirst_BoundedByCount()
    {
        var (db, conn) = BuildRelationalContext();
        using var _ = db;
        using var __ = conn;

        db.StatusCheckSet.AddRange(
            new StatusCheck { Id = 1, Title = "alpha", StatusCheckUrl = "x", ServiceLogoUrl = "z" },
            new StatusCheck { Id = 2, Title = "bravo", StatusCheckUrl = "x", ServiceLogoUrl = "z" }
        );
        var baseUtc = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 10; i++)
        {
            db.HistoricalStatusDataSet.Add(new HistoricalStatusData
            {
                StatusCheckId = 1,
                TimeOfCheckUTC = baseUtc.AddMinutes(i),
            });
        }
        // Noise from a different check to prove the WHERE filter.
        for (int i = 0; i < 3; i++)
        {
            db.HistoricalStatusDataSet.Add(new HistoricalStatusData
            {
                StatusCheckId = 2,
                TimeOfCheckUTC = baseUtc.AddMinutes(i),
            });
        }
        await db.SaveChangesAsync();

        var repo = new HistoricalStatusDataRepository(db);
        var ticks = await repo.GetRecentTicks(1, 5);

        Assert.AreEqual(5, ticks.Count);
        Assert.IsTrue(ticks.All(t => t.StatusCheckId == 1));
        for (int i = 0; i < ticks.Count - 1; i++)
        {
            Assert.IsTrue(ticks[i].TimeOfCheckUTC > ticks[i + 1].TimeOfCheckUTC, "Order must be newest-first.");
        }
        Assert.AreEqual(baseUtc.AddMinutes(9), ticks[0].TimeOfCheckUTC);
    }

    [TestMethod]
    public async Task EmptyHistory_ReturnsEmptyList()
    {
        var (db, conn) = BuildRelationalContext();
        using var _ = db;
        using var __ = conn;
        db.StatusCheckSet.Add(new StatusCheck { Id = 7, Title = "x", StatusCheckUrl = "x", ServiceLogoUrl = "x" });
        await db.SaveChangesAsync();

        var repo = new HistoricalStatusDataRepository(db);
        var ticks = await repo.GetRecentTicks(7, 20);

        Assert.AreEqual(0, ticks.Count);
    }

    [TestMethod]
    public async Task CountClampedToSafeMaximum()
    {
        var (db, conn) = BuildRelationalContext();
        using var _ = db;
        using var __ = conn;
        db.StatusCheckSet.Add(new StatusCheck { Id = 1, Title = "x", StatusCheckUrl = "x", ServiceLogoUrl = "x" });
        for (int i = 0; i < 600; i++)
        {
            db.HistoricalStatusDataSet.Add(new HistoricalStatusData
            {
                StatusCheckId = 1,
                TimeOfCheckUTC = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
            });
        }
        await db.SaveChangesAsync();

        var repo = new HistoricalStatusDataRepository(db);
        var ticks = await repo.GetRecentTicks(1, 100_000);

        // Repo clamps to 500 to protect against UI-side overflow.
        Assert.AreEqual(500, ticks.Count);
    }
}
