using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.ApiService;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.IncidentOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #106 PR2 — incident severity UI + the write path that backs it:
/// AddOrUpdateIncident's ResolvedUtc lifecycle (which feeds MTTR), the public
/// API severity field, and the IncidentList severity styling.
/// </summary>
[TestClass]
public class IncidentSeverityUiTests
{
    // ---- AddOrUpdateIncident: ResolvedUtc lifecycle ----------------------

    private static (IncidentService svc, SuperStatusDb db, SqliteConnection conn) Build()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (new IncidentService(new IncidentRepository(db)), db, conn);
    }

    [TestMethod]
    public async Task Create_Unresolved_NoResolvedUtc_PersistsSeverity()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var vm = await svc.AddOrUpdateIncident(new IncidentViewModel
        { Title = "elevated latency", Severity = IncidentSeverity.Severe, VisibleToPublic = true, Resolved = false });

        Assert.IsTrue(vm.Id > 0);
        Assert.IsNull(vm.ResolvedUtc);
        Assert.AreEqual(IncidentSeverity.Severe, vm.Severity);
        Assert.AreNotEqual(default, vm.Created);
    }

    [TestMethod]
    public async Task Create_Resolved_StampsResolvedUtc()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var vm = await svc.AddOrUpdateIncident(new IncidentViewModel { Title = "x", Resolved = true });
        Assert.IsNotNull(vm.ResolvedUtc);
    }

    [TestMethod]
    public async Task ResolveTransition_StampsResolvedUtc_ReopenClearsIt()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var open = await svc.AddOrUpdateIncident(new IncidentViewModel { Title = "x", Resolved = false });
        Assert.IsNull(open.ResolvedUtc);

        var resolved = await svc.AddOrUpdateIncident(new IncidentViewModel { Id = open.Id, Title = "x", Resolved = true });
        Assert.IsNotNull(resolved.ResolvedUtc);

        var reopened = await svc.AddOrUpdateIncident(new IncidentViewModel { Id = open.Id, Title = "x", Resolved = false });
        Assert.IsNull(reopened.ResolvedUtc, "reopening must clear ResolvedUtc so MTTR never counts it");
    }

    [TestMethod]
    public async Task EditAlreadyResolved_PreservesOriginalResolvedUtc()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var resolved = await svc.AddOrUpdateIncident(new IncidentViewModel { Title = "x", Resolved = true });
        var stamp = resolved.ResolvedUtc;

        // Edit the title while still resolved — the stamp must not move.
        var edited = await svc.AddOrUpdateIncident(new IncidentViewModel
        { Id = resolved.Id, Title = "x (updated)", Resolved = true, Severity = IncidentSeverity.Critical });

        Assert.AreEqual(stamp, edited.ResolvedUtc, "editing a resolved incident must preserve the original ResolvedUtc");
        Assert.AreEqual(IncidentSeverity.Critical, edited.Severity);
    }

    // ---- public API severity field --------------------------------------

    [TestMethod]
    public void PublicIncidentDto_SerializesSeverity_AsSnakeCaseLowercase()
    {
        var dto = new PublicStatusOpenIncidentDto(Id: 1, Title: "t", StartedUtc: DateTime.UtcNow, Severity: "critical");
        string json = JsonSerializer.Serialize(dto);
        StringAssert.Contains(json, "\"severity\":\"critical\"");
    }

    // ---- IncidentList severity styling -----------------------------------

    [TestMethod]
    public void IncidentList_AppliesSeverityFrameClass_AndBadge()
    {
        // severity 2 = Critical → .severe frame + red badge LED; 0 = Minor → .minor + amber.
        const string json = """
        {"2026-05-28T00:00:00":[
            {"id":1,"title":"DB outage","description":"down","resolved":false,"created":"2026-05-28T19:05:00Z","visibleToPublic":true,"severity":2},
            {"id":2,"title":"slow page","description":"meh","resolved":false,"created":"2026-05-28T09:19:00Z","visibleToPublic":true,"severity":0}
        ]}
        """;
        using var ctx = new BunitTestContext();
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        // IncidentList injects IDialogService (#159 Manage mode); register Mud.
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.RenderComponent<IncidentList>();

        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".incident").Count));
        cut.Find(".incident.severe");                 // Critical → red frame
        cut.Find(".incident.minor");                  // Minor → amber frame
        cut.Find(".incident.severe .tag .led.down");  // Critical badge LED red
        Assert.IsTrue(cut.Markup.Contains("CRITICAL"));
        Assert.IsTrue(cut.Markup.Contains("MINOR"));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
