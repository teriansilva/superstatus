using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;
using SuperStatus.Web;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #164 — status-check delete. Proves the relational cascade (children
/// are removed with the check), the service's found/not-found result, and the
/// client's 204→true / 404→false mapping.
/// </summary>
[TestClass]
public class StatusCheckDeleteTests
{
    private static StatusCheckService Svc(SuperStatusDb db) =>
        new(new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new StubFactory(), NullLogger<StatusCheckService>.Instance);

    private static StatusCheck NewCheck(string title = "check") => new()
    {
        Title = title, StatusCheckUrl = "http://x/health", ServiceLogoUrl = "",
        ExpectedStatusCode = 200, Enabled = true, Created = DateTime.UtcNow,
    };

    private static Incident LinkedIncident(long checkId, bool auto) => new()
    {
        Title = "Cayoo AI is experiencing an outage",
        AuotmaticallyGeneratedReport = auto, VisibleToPublic = true,
        Resolved = false, SourceStatusCheckId = checkId, Created = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task Delete_RemovesCheck_AndCascadesChildren()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var check = new StatusCheck
        {
            Title = "to delete", StatusCheckUrl = "http://x/health",
            ServiceLogoUrl = "",
            ExpectedStatusCode = 200, Enabled = true,
            Created = DateTime.UtcNow,
        };
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();
        long id = check.Id;

        // One of each check-owned child row.
        var hist = new HistoricalStatusData(new StatusCheckResult(check, 0, 0, true), FailType.Unreachable) { StatusCheckId = id };
        db.HistoricalStatusDataSet.Add(hist);
        db.DailyStatusRollupSet.Add(new DailyStatusRollup { StatusCheckId = id, Day = DateTime.UtcNow.Date, Total = 10, Down = 1, Degraded = 0, Unreachable = 0 });
        db.WebhookExecutionLogSet.Add(new WebhookExecutionLog { StatusCheckId = id, AttemptedUtc = DateTime.UtcNow, TargetUrl = "http://hook", HttpStatusCode = 200, ResponseTimeMs = 10 });
        await db.SaveChangesAsync();

        bool ok = await Svc(db).DeleteStatusCheckAsync(id);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, await db.StatusCheckSet.CountAsync(), "the check is gone");
        Assert.AreEqual(0, await db.HistoricalStatusDataSet.CountAsync(), "historical ticks cascade");
        Assert.AreEqual(0, await db.DailyStatusRollupSet.CountAsync(), "daily rollups cascade");
        Assert.AreEqual(0, await db.WebhookExecutionLogSet.CountAsync(), "webhook logs cascade");
    }

    [TestMethod]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        Assert.IsFalse(await Svc(db).DeleteStatusCheckAsync(999), "no such check → false (→ 404 at the API)");
    }

    // #415 — happy path: deleting a check removes its open linked incident with it
    // (ON DELETE CASCADE), so the public page can't be stuck showing a phantom outage
    // for a check that no longer exists.
    [TestMethod]
    public async Task Delete_CascadeRemovesLinkedIncident()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var check = NewCheck("ai");
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();
        db.IncidentSet.Add(LinkedIncident(check.Id, auto: true));
        await db.SaveChangesAsync();

        bool ok = await Svc(db).DeleteStatusCheckAsync(check.Id);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, await db.StatusCheckSet.CountAsync(), "the check is gone");
        Assert.AreEqual(0, await db.IncidentSet.CountAsync(), "its open incident is removed with it — no phantom outage survives");
    }

    // #415 — scope guard: an incident NOT linked to the deleted check (null source —
    // the normal manual-incident case) is untouched; only linked incidents cascade.
    [TestMethod]
    public async Task Delete_LeavesUnlinkedIncident()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var check = NewCheck("unrelated");
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();
        db.IncidentSet.Add(new Incident
        {
            Title = "manual", AuotmaticallyGeneratedReport = false, VisibleToPublic = true,
            Resolved = false, SourceStatusCheckId = null, Created = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await Svc(db).DeleteStatusCheckAsync(check.Id);

        Assert.AreEqual(1, await db.IncidentSet.CountAsync(), "an incident with no link to the deleted check is untouched");
    }

    // #415 — scope guard: deleting one check never cascades to another check's
    // incident (the cascade is keyed on SourceStatusCheckId).
    [TestMethod]
    public async Task Delete_DoesNotCascadeToOtherChecksIncident()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var toDelete = NewCheck("A");
        var keep = NewCheck("B");
        db.StatusCheckSet.AddRange(toDelete, keep);
        await db.SaveChangesAsync();
        db.IncidentSet.Add(LinkedIncident(keep.Id, auto: true));
        await db.SaveChangesAsync();

        await Svc(db).DeleteStatusCheckAsync(toDelete.Id);

        var incident = await db.IncidentSet.AsNoTracking().SingleAsync();
        Assert.AreEqual(keep.Id, incident.SourceStatusCheckId, "check B's incident is untouched when check A is deleted");
        Assert.IsFalse(incident.Resolved, "and stays open");
    }

    // #415 — the delete-during-draft race, insert-AFTER-delete arm: once a check is
    // deleted, the FK (ON DELETE CASCADE) rejects committing a NEW auto-incident for
    // it, so an in-flight AutoIncidentWorker draft cannot recreate the orphan.
    [TestMethod]
    public async Task CreateAutoIncident_ForDeletedCheck_IsRejected_NoOrphan()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var check = NewCheck("racy");
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();
        long deletedId = check.Id;
        await Svc(db).DeleteStatusCheckAsync(deletedId);

        var incidents = new IncidentService(new IncidentRepository(db));
        await Assert.ThrowsExceptionAsync<DbUpdateException>(() =>
            incidents.CreateAutoIncidentAsync(deletedId, new IncidentDraft("late draft", "d", IncidentSeverity.Minor, false)));
        Assert.AreEqual(0, await db.IncidentSet.CountAsync(), "no orphaned incident is committed for a deleted check");
    }

    // #415 — the chosen CASCADE trade-off: a deleted check's incident history (even
    // resolved rows) is removed with it, not retained. Documents the behaviour the
    // maintainer picked over history-preserving SET NULL.
    [TestMethod]
    public async Task Delete_CascadeRemovesResolvedIncidentHistory()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var check = NewCheck("history");
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();
        var resolved = LinkedIncident(check.Id, auto: true);
        resolved.Resolved = true;
        resolved.ResolvedUtc = DateTime.UtcNow;
        db.IncidentSet.Add(resolved);
        await db.SaveChangesAsync();

        await Svc(db).DeleteStatusCheckAsync(check.Id);

        Assert.AreEqual(0, await db.IncidentSet.CountAsync(), "a deleted check's incident history is removed with it (CASCADE)");
    }

    [TestMethod]
    public async Task Client_Delete_Maps204ToTrue_And404ToFalse()
    {
        var ok = new StatusApiClient(new HttpClient(new CodeStub(HttpStatusCode.NoContent)) { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue(await ok.DeleteStatusCheckAsync(1));

        var missing = new StatusApiClient(new HttpClient(new CodeStub(HttpStatusCode.NotFound)) { BaseAddress = new Uri("http://api.test") });
        Assert.IsFalse(await missing.DeleteStatusCheckAsync(2));
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new CodeStub(HttpStatusCode.OK));
    }

    private sealed class CodeStub : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        public CodeStub(HttpStatusCode code) { _code = code; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent("", Encoding.UTF8, "application/json") });
    }
}
