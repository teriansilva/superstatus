using System.Net;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using SuperStatus.Web.Components.IncidentOverview;
using SuperStatus.Web.Components.StatusCheckOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #159 Phase 2 — editing moved to the operator console; the public
/// status page is read-only. Verifies operator controls are ABSENT on the
/// public service card (anonymous and authorized) and PRESENT on the operator
/// management surfaces.
/// </summary>
[TestClass]
public class EditingMoveTests
{
    private static BunitTestContext Ctx(string json)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static StatusCheckViewModel SampleCheck() => new()
    {
        Id = 1, Title = "superstatus.io public API",
        StatusCheckUrl = "https://superstatus.io/health", Enabled = true,
    };

    // ---- public service card is read-only ---------------------------------

    [TestMethod]
    public void PublicCard_Anonymous_HasNoOperatorControls_OnlyDetailLink()
    {
        using var ctx = Ctx("[]");
        var cut = ctx.RenderComponent<StatusCheckOverviewCard>(p => p.Add(x => x.statusCheck, SampleCheck()));

        Assert.AreEqual(0, cut.FindAll("button").Count, "Public card must render no operator buttons.");
        Assert.AreEqual(0, cut.FindAll(".mud-menu").Count, "Public card must render no operator menu.");
        var link = cut.Find(".actions a");
        Assert.AreEqual("/status/1", link.GetAttribute("href"));
    }

    [TestMethod]
    public void PublicCard_Authorized_StillHasNoOperatorControls()
    {
        using var ctx = Ctx("[]");
        ctx.AddTestAuthorization().SetAuthorized("operator");
        var cut = ctx.RenderComponent<StatusCheckOverviewCard>(p => p.Add(x => x.statusCheck, SampleCheck()));

        // The page is lean for everyone now — signing in doesn't add inline edit.
        Assert.AreEqual(0, cut.FindAll("button").Count);
        Assert.AreEqual(0, cut.FindAll(".mud-menu").Count);
    }

    // ---- operator console exposes the editing actions ---------------------

    [TestMethod]
    public void AdminList_RendersAddAndPerRowOperatorActions()
    {
        const string checksJson = """
        {"results":[
            {"id":1,"title":"superstatus.io public API","statusCheckUrl":"https://superstatus.io/health","enabled":true,"intervalSeconds":30},
            {"id":2,"title":"Mail relay","statusCheckUrl":"https://mail.example/health","enabled":false,"intervalSeconds":120}
        ],"totalCount":2,"page":1,"pageSize":50}
        """;
        using var ctx = Ctx(checksJson);
        var cut = ctx.RenderComponent<StatusCheckAdminList>();

        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".op-row").Count));
        Assert.IsTrue(cut.Markup.Contains("Add check"));
        // Per-row operator actions present.
        Assert.IsTrue(cut.Markup.Contains(">Run<"));
        Assert.IsTrue(cut.Markup.Contains(">Edit<"));
        // Enabled check → "Pause"; disabled check → "Resume".
        Assert.IsTrue(cut.Markup.Contains(">Pause<"));
        Assert.IsTrue(cut.Markup.Contains(">Resume<"));
        // #164: destructive Delete action present on the operator row.
        Assert.IsTrue(cut.Markup.Contains(">Delete<"));
    }

    // ---- incident list: manage vs read-only -------------------------------

    private const string IncidentsJson = """
    {"2026-05-28T00:00:00":[
        {"id":1,"title":"Elevated latency","description":"P95 over budget","resolved":false,"created":"2026-05-28T19:05:00Z","visibleToPublic":true}
    ]}
    """;

    [TestMethod]
    public void IncidentList_ManageMode_HasReportAndEdit()
    {
        using var ctx = Ctx(IncidentsJson);
        var cut = ctx.RenderComponent<IncidentList>(p => p.Add(x => x.Manage, true));

        cut.WaitForAssertion(() => cut.Find(".incident"));
        Assert.IsTrue(cut.Markup.Contains("Report incident"), "Manage mode exposes Report.");
        Assert.AreEqual(1, cut.FindAll(".inc-actions").Count, "Manage mode exposes per-incident Edit.");
    }

    [TestMethod]
    public void IncidentList_PublicMode_HasNoManageControls()
    {
        using var ctx = Ctx(IncidentsJson);
        var cut = ctx.RenderComponent<IncidentList>();

        cut.WaitForAssertion(() => cut.Find(".incident"));
        Assert.IsFalse(cut.Markup.Contains("Report incident"), "Public log has no Report.");
        Assert.AreEqual(0, cut.FindAll(".inc-actions").Count, "Public log has no Edit actions.");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) { _json = json; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            // The card's history graph + the admin list + the incident list each
            // hit a different GET; route by path so one stub serves all.
            var json = path switch
            {
                "/statuscheck" => _json.StartsWith("{\"results") ? _json : """{"results":[],"totalCount":0,"page":1,"pageSize":50}""",
                "/incidents"   => _json.StartsWith("{\"2026") ? _json : "{}",
                _              => _json.StartsWith("[") ? _json : "[]",   // history graph etc.
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
