using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #168 Phase 1 — schema for AI-authored incidents: the per-check
/// AutoIncidentEnabled opt-in round-trips through the edit view-model, and the
/// partial unique index enforces "at most one OPEN auto-generated incident per
/// source check" at the DB boundary (so concurrent scheduler ticks can't draft
/// duplicates), while leaving manual and resolved incidents unconstrained.
/// </summary>
[TestClass]
public class AutoIncidentSchemaTests
{
    // One shared in-memory DB across contexts; the connection stays open for the test.
    private static SqliteConnection OpenDb()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return conn;
    }

    private static SuperStatusDb Ctx(SqliteConnection conn)
        => new(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);

    private static Incident Auto(long checkId, bool resolved = false) => new()
    {
        Title = "auto", Description = "x", AuotmaticallyGeneratedReport = true,
        SourceStatusCheckId = checkId, Resolved = resolved, Created = DateTime.UtcNow,
        ResolvedUtc = resolved ? DateTime.UtcNow : null,
    };

    [TestMethod]
    public void AutoIncidentEnabled_RoundTripsThroughViewModel()
    {
        var entity = new StatusCheck
        {
            Title = "probe", StatusCheckUrl = "https://x/health", WebHookOnErrorUrl = "",
            ServiceLogoUrl = "", Enabled = true, AutoIncidentEnabled = true,
        };
        var vm = new StatusCheckViewModelBase(entity);
        Assert.IsTrue(vm.AutoIncidentEnabled, "the edit VM surfaces the per-check opt-in");

        var fresh = new StatusCheckViewModelBase();
        Assert.IsFalse(fresh.AutoIncidentEnabled, "a new check defaults the opt-in off");
    }

    [TestMethod]
    public async Task PartialUniqueIndex_BlocksSecondOpenAutoIncident_ForSameCheck()
    {
        using var conn = OpenDb();
        using (var db = Ctx(conn))
        {
            db.IncidentSet.Add(Auto(1));
            await db.SaveChangesAsync();
        }
        using (var db = Ctx(conn))
        {
            db.IncidentSet.Add(Auto(1)); // a second OPEN auto-incident for check 1
            await Assert.ThrowsExceptionAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [TestMethod]
    public async Task PartialUniqueIndex_AllowsDifferentChecks_AndAfterResolution()
    {
        using var conn = OpenDb();
        using (var db = Ctx(conn))
        {
            db.IncidentSet.Add(Auto(1));
            db.IncidentSet.Add(Auto(2)); // different check — fine
            await db.SaveChangesAsync();
        }

        // Resolve check 1's open auto-incident, then a new open one for check 1 is allowed.
        using (var db = Ctx(conn))
        {
            var open = await db.IncidentSet.SingleAsync(i => i.SourceStatusCheckId == 1 && !i.Resolved);
            open.Resolved = true; open.ResolvedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        using (var db = Ctx(conn))
        {
            db.IncidentSet.Add(Auto(1)); // previous one is resolved → no longer covered by the filter
            await db.SaveChangesAsync();
            Assert.AreEqual(2, await db.IncidentSet.CountAsync(i => i.SourceStatusCheckId == 1));
        }
    }

    [TestMethod]
    public async Task PartialUniqueIndex_DoesNotConstrainManualOrNullSource()
    {
        using var conn = OpenDb();
        using var db = Ctx(conn);

        // Manual incidents (not auto-generated) sharing a source id are unconstrained.
        db.IncidentSet.Add(new Incident { Title = "m1", AuotmaticallyGeneratedReport = false, SourceStatusCheckId = 5, Created = DateTime.UtcNow });
        db.IncidentSet.Add(new Incident { Title = "m2", AuotmaticallyGeneratedReport = false, SourceStatusCheckId = 5, Created = DateTime.UtcNow });
        // Null-source incidents (the normal manual case) are unconstrained too.
        db.IncidentSet.Add(new Incident { Title = "n1", AuotmaticallyGeneratedReport = true, SourceStatusCheckId = null, Created = DateTime.UtcNow });
        db.IncidentSet.Add(new Incident { Title = "n2", AuotmaticallyGeneratedReport = true, SourceStatusCheckId = null, Created = DateTime.UtcNow });

        await db.SaveChangesAsync(); // must not throw
        Assert.AreEqual(4, await db.IncidentSet.CountAsync());
    }
}
