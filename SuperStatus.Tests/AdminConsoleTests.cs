using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #95 Phase 4 + #159 Phase 2 — operator console. Renders against
/// path-aware stubs; asserts the calm telemetry strip plus the relocated
/// editing surfaces (Status checks // Manage, Incidents // Manage) and that
/// the old "Where things live" explainer is gone.
/// </summary>
[TestClass]
public class AdminConsoleTests
{
    private static BunitTestContext CtxWith(string summaryJson, HttpStatusCode summaryCode = HttpStatusCode.OK)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new PathStub(summaryJson, summaryCode)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        // #167 Phase 2: the console now hosts SiteSettingsPanel, which injects
        // this client. The PathStub's catch-all returns {} for GET /settings.
        ctx.Services.AddSingleton(new SettingsApiClient(http));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private const string SummaryJson = """
    {"services":{"up":14,"degraded":1,"down":0,"total":15},
     "latency_ms":{"avg":142,"p95":387},
     "uptime_30d_pct":99.94,"incidents_30d":3,
     "per_service":[],"overall":"up","generated_utc":"2026-05-29T07:00:00Z"}
    """;

    [TestMethod]
    public void RendersConsoleHeader_Telemetry_AndManagePanels()
    {
        using var ctx = CtxWith(SummaryJson);
        var cut = ctx.RenderComponent<Admin>();

        cut.WaitForAssertion(() => cut.Find(".telemetry-strip"));
        cut.Find(".panel.primary");
        Assert.IsTrue(cut.Markup.Contains("Operator overview"));
        Assert.IsTrue(cut.Markup.Contains("99.94%"));

        // #159: editing moved here — manage panels with their add affordances.
        Assert.IsTrue(cut.Markup.Contains("STATUS CHECKS"), "Status-check manage panel should render.");
        Assert.IsTrue(cut.Markup.Contains("Add check"), "Operator can add a check from the console.");
        Assert.IsTrue(cut.Markup.Contains("Report incident"), "Operator can report an incident from the console.");

        // The old explainer is gone.
        Assert.IsFalse(cut.Markup.Contains("Where things live"));
        Assert.IsFalse(cut.Markup.Contains("Run now / pause / resume"));
        Assert.IsFalse(cut.Markup.Contains("I agree"));
    }

    [TestMethod]
    public void SummaryUnavailable_StillRendersManagePanels()
    {
        using var ctx = CtxWith("", HttpStatusCode.InternalServerError);
        var cut = ctx.RenderComponent<Admin>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Telemetry unavailable")));
        // Manage panels render even when the summary telemetry is down.
        Assert.IsTrue(cut.Markup.Contains("STATUS CHECKS"));
        Assert.IsFalse(cut.Markup.Contains("Where things live"));
    }

    // Path-aware stub: summary endpoint returns the given json/code; the
    // status-check list, incident map and webhook log return empty payloads so
    // the manage panels render their empty states.
    private sealed class PathStub : HttpMessageHandler
    {
        private readonly string _summary;
        private readonly HttpStatusCode _summaryCode;
        public PathStub(string summary, HttpStatusCode summaryCode) { _summary = summary; _summaryCode = summaryCode; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            (HttpStatusCode code, string json) = path switch
            {
                "/statuscheck/summary" => (_summaryCode, _summary),
                "/statuscheck"         => (HttpStatusCode.OK, """{"results":[],"totalCount":0,"page":1,"pageSize":50}"""),
                "/incidents"           => (HttpStatusCode.OK, "{}"),
                // #181: Admin now checks onboarding; a set OnboardedUtc makes it
                // render the console (not the first-run wizard).
                "/settings"            => (HttpStatusCode.OK, """{"onboardedUtc":"2026-01-01T00:00:00Z"}"""),
                var p when p.StartsWith("/admin/webhook-log") => (HttpStatusCode.OK, "[]"),
                _                      => (HttpStatusCode.OK, "{}"),
            };
            var resp = new HttpResponseMessage(code);
            if (!string.IsNullOrEmpty(json)) resp.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }
}
