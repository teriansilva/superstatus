using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #106 — incident severity + ResolvedUtc + MTTR (schema/service
/// slice). SQLite-backed so the queries run against a relational provider.
/// </summary>
[TestClass]
public class IncidentSeverityMttrTests
{
    private (SuperStatusDb db, SqliteConnection conn) Build()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static Incident Inc(long id, bool resolved, DateTime created, DateTime? resolvedUtc, IncidentSeverity sev = IncidentSeverity.Minor)
        => new() { Id = id, Title = $"i{id}", Resolved = resolved, Created = created, ResolvedUtc = resolvedUtc, Severity = sev, VisibleToPublic = true };

    [TestMethod]
    public void Model_PersistsSeverityAndResolvedUtc()
    {
        var (db, conn) = Build();
        using var _ = db; using var __ = conn;
        var now = DateTime.UtcNow;
        db.IncidentSet.Add(Inc(1, true, now.AddHours(-2), now.AddHours(-1), IncidentSeverity.Critical));
        db.SaveChanges();

        var loaded = db.IncidentSet.Single();
        Assert.AreEqual(IncidentSeverity.Critical, loaded.Severity);
        Assert.IsNotNull(loaded.ResolvedUtc);
    }

    [TestMethod]
    public async Task GetResolvedIncidentsInWindow_ExcludesOpenAndNullResolvedUtcAndOutOfWindow()
    {
        var (db, conn) = Build();
        using var _ = db; using var __ = conn;
        var now = DateTime.UtcNow;
        db.IncidentSet.AddRange(
            Inc(1, true,  now.AddDays(-2), now.AddDays(-2).AddHours(1)),   // counts
            Inc(2, false, now.AddDays(-1), null),                          // open → excluded
            Inc(3, true,  now.AddDays(-40), now.AddDays(-40).AddHours(1)), // out of 30d window → excluded
            Inc(4, true,  now.AddDays(-3), null)                          // resolved pre-ResolvedUtc → excluded
        );
        await db.SaveChangesAsync();
        var repo = new IncidentRepository(db);

        var rows = await repo.GetResolvedIncidentsInWindow(30);
        CollectionAssert.AreEquivalent(new long[] { 1 }, rows.Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public async Task GetMttr_AveragesMeasuredDurations()
    {
        var (db, conn) = Build();
        using var _ = db; using var __ = conn;
        var now = DateTime.UtcNow;
        // durations: 1h and 3h → mean 2h.
        db.IncidentSet.AddRange(
            Inc(1, true, now.AddHours(-5), now.AddHours(-4)),  // 1h
            Inc(2, true, now.AddHours(-6), now.AddHours(-3))   // 3h
        );
        await db.SaveChangesAsync();
        var svc = new IncidentService(new IncidentRepository(db));

        var mttr = await svc.GetMttrAsync(30);
        Assert.IsNotNull(mttr);
        Assert.AreEqual(2.0, mttr!.Value.TotalHours, 0.01);
    }

    [TestMethod]
    public async Task GetMttr_NoResolvedIncidents_ReturnsNull()
    {
        var (db, conn) = Build();
        using var _ = db; using var __ = conn;
        db.IncidentSet.Add(Inc(1, false, DateTime.UtcNow.AddHours(-1), null));
        await db.SaveChangesAsync();
        var svc = new IncidentService(new IncidentRepository(db));

        Assert.IsNull(await svc.GetMttrAsync(30));
    }

    [TestMethod]
    public void Severity_EnumValuesAreStable()
    {
        // Persisted as int — reordering breaks existing rows.
        Assert.AreEqual(0, (int)IncidentSeverity.Minor);
        Assert.AreEqual(1, (int)IncidentSeverity.Severe);
        Assert.AreEqual(2, (int)IncidentSeverity.Critical);
    }
}
