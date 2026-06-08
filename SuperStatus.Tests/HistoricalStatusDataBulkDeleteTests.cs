using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #80: cleanup must run as a single bulk DELETE instead of loading
/// every expired row into memory + ClientCascade-per-parent. The
/// behavioural tests run against SQLite-in-memory because the EF Core
/// InMemory provider does not implement ExecuteDeleteAsync; the model
/// metadata test runs against any provider.
/// </summary>
[TestClass]
public class HistoricalStatusDataBulkDeleteTests
{
    // SQLite-in-memory connection (relational provider, supports
    // ExecuteDeleteAsync + real FK cascade). The connection stays open
    // for the lifetime of the context.
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

    private SuperStatusDb BuildInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<SuperStatusDb>()
            .UseInMemoryDatabase($"db-{Guid.NewGuid()}")
            .Options;
        return new SuperStatusDb(options);
    }

    [TestMethod]
    public async Task BulkDelete_RemovesOnlyRowsOlderThanCutoff()
    {
        var (db, conn) = BuildRelationalContext();
        using var _ = db;
        using var __ = conn;
        var now = DateTime.UtcNow;
        db.StatusCheckSet.AddRange(
            new StatusCheck { Id = 1, Title = "alpha", StatusCheckUrl = "x", WebHookOnErrorUrl = "y", ServiceLogoUrl = "z" },
            new StatusCheck { Id = 2, Title = "bravo", StatusCheckUrl = "x", WebHookOnErrorUrl = "y", ServiceLogoUrl = "z" }
        );
        db.HistoricalStatusDataSet.AddRange(
            new HistoricalStatusData { StatusCheckId = 1, TimeOfCheckUTC = now.AddDays(-40) }, // expired
            new HistoricalStatusData { StatusCheckId = 1, TimeOfCheckUTC = now.AddDays(-35) }, // expired
            new HistoricalStatusData { StatusCheckId = 1, TimeOfCheckUTC = now.AddDays(-31) }, // expired
            new HistoricalStatusData { StatusCheckId = 1, TimeOfCheckUTC = now.AddDays(-29) }, // keep
            new HistoricalStatusData { StatusCheckId = 1, TimeOfCheckUTC = now.AddDays(-7)  }, // keep
            new HistoricalStatusData { StatusCheckId = 2, TimeOfCheckUTC = now.AddSeconds(-30) } // keep
        );
        await db.SaveChangesAsync();
        var repo = new HistoricalStatusDataRepository(db);

        int deleted = await repo.BulkDeleteOlderThanXDaysAsync(30);

        Assert.AreEqual(3, deleted, "All three rows older than 30 days must be deleted.");
        Assert.AreEqual(3, await db.HistoricalStatusDataSet.CountAsync());
        Assert.IsFalse(await db.HistoricalStatusDataSet
            .AnyAsync(x => x.TimeOfCheckUTC < now.AddDays(-30)));
    }

    [TestMethod]
    public async Task BulkDelete_NoExpiredRows_ReturnsZeroAndChangesNothing()
    {
        var (db, conn) = BuildRelationalContext();
        using var _ = db;
        using var __ = conn;
        var now = DateTime.UtcNow;
        db.StatusCheckSet.Add(new StatusCheck { Id = 1, Title = "alpha", StatusCheckUrl = "x", WebHookOnErrorUrl = "y", ServiceLogoUrl = "z" });
        db.HistoricalStatusDataSet.AddRange(
            new HistoricalStatusData { StatusCheckId = 1, TimeOfCheckUTC = now.AddDays(-1) },
            new HistoricalStatusData { StatusCheckId = 1, TimeOfCheckUTC = now.AddDays(-5) }
        );
        await db.SaveChangesAsync();
        var repo = new HistoricalStatusDataRepository(db);

        int deleted = await repo.BulkDeleteOlderThanXDaysAsync(30);

        Assert.AreEqual(0, deleted);
        Assert.AreEqual(2, await db.HistoricalStatusDataSet.CountAsync());
    }

    [TestMethod]
    public void Model_HistoricalStatusAction_Cascade_IsCascade()
    {
        // Issue #80: cleanup uses ExecuteDeleteAsync which bypasses EF's
        // change tracker. The FK from HistoricalStatusAction to
        // HistoricalStatusData must use DB-side CASCADE so removing a
        // parent also removes its action; previously this was
        // ClientCascade (EF-side) which ExecuteDeleteAsync does not run.
        using var db = BuildInMemoryContext();
        var fk = db.Model.FindEntityType(typeof(HistoricalStatusAction))?
            .GetForeignKeys()
            .FirstOrDefault(f => f.PrincipalEntityType.ClrType == typeof(HistoricalStatusData));

        Assert.IsNotNull(fk, "Expected an FK on HistoricalStatusAction pointing at HistoricalStatusData.");
        Assert.AreEqual(DeleteBehavior.Cascade, fk!.DeleteBehavior);
    }
}
