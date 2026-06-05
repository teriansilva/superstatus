using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.IncidentOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #95 Phase 3b — IncidentList tactical reskin. Renders against a
/// stub /incidents response and asserts the HUD vocabulary (.incident,
/// .incident-day, open/resolved tag + dim) without a live API.
/// </summary>
[TestClass]
public class IncidentListReskinTests
{
    private static BunitTestContext CtxWith(string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new StubHandler(code, json)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        // IncidentList injects IDialogService (#159 Manage mode); register Mud.
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // One day with an open incident + a resolved incident.
    private const string TwoIncidentsJson = """
    {"2026-05-28T00:00:00":[
        {"id":1,"title":"AI proxy elevated latency","description":"P95 over budget","resolved":false,"created":"2026-05-28T19:05:00Z","visibleToPublic":true},
        {"id":2,"title":"Runner pool offline","description":"Quartz pileup","resolved":true,"created":"2026-05-28T09:19:00Z","visibleToPublic":true}
    ]}
    """;

    private const string EmptyJson = "{}";

    [TestMethod]
    public void RendersDayHeader_OpenAndResolvedIncidents()
    {
        using var ctx = CtxWith(TwoIncidentsJson);
        var cut = ctx.RenderComponent<IncidentList>();

        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".incident").Count));
        cut.Find(".incident-day");
        // Open incident → degraded tag; resolved → up tag + .resolved dim.
        cut.Find(".incident .tag .led.degraded");
        cut.Find(".incident.resolved .tag .led.up");
        Assert.IsTrue(cut.Markup.Contains("AI proxy elevated latency"));
        Assert.IsTrue(cut.Markup.Contains("RESOLVED"));
        Assert.IsTrue(cut.Markup.Contains("OPEN"));
    }

    [TestMethod]
    public void EmptySet_RendersAllClearState()
    {
        using var ctx = CtxWith(EmptyJson);
        var cut = ctx.RenderComponent<IncidentList>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("None in the last 30 days")));
        Assert.AreEqual(0, cut.FindAll(".incident").Count);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _json;
        public StubHandler(HttpStatusCode code, string json) { _code = code; _json = json; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
    }
}
