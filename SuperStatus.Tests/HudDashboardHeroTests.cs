using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Web;
using SuperStatus.Web.Components.StatusCheckOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #95 Phase 3a — Home hero summary panel. Renders against a stub
/// HttpMessageHandler so the /statuscheck/summary contract drives the
/// hero's accent + telemetry without a live API.
/// </summary>
[TestClass]
public class HudDashboardHeroTests
{
    private static BunitTestContext CtxWithSummaryJson(string? json, HttpStatusCode code = HttpStatusCode.OK)
    {
        var ctx = new BunitTestContext();
        var handler = new StubHandler(json, code);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return ctx;
    }

    private const string UpJson = """
    {"services":{"up":14,"degraded":1,"down":0,"total":15},
     "latency_ms":{"avg":142,"p95":387},
     "uptime_30d_pct":99.94,"incidents_30d":3,
     "per_service":[],"overall":"up","generated_utc":"2026-05-29T07:00:00Z"}
    """;

    private const string DownJson = """
    {"services":{"up":12,"degraded":1,"down":2,"total":15},
     "latency_ms":{"avg":412,"p95":2100},
     "uptime_30d_pct":97.82,"incidents_30d":7,
     "per_service":[],"overall":"down","generated_utc":"2026-05-29T07:00:00Z"}
    """;

    private const string EmptyJson = """
    {"services":{"up":0,"degraded":0,"down":0,"total":0},
     "latency_ms":{"avg":null,"p95":null},
     "uptime_30d_pct":0,"incidents_30d":0,
     "per_service":[],"overall":"up","generated_utc":"2026-05-29T07:00:00Z"}
    """;

    [TestMethod]
    public void Up_RendersPrimaryHero_WithHealthyCallsignAndCounts()
    {
        using var ctx = CtxWithSummaryJson(UpJson);
        var cut = ctx.RenderComponent<HudDashboardHero>();

        cut.WaitForAssertion(() => cut.Find(".panel.primary"));
        Assert.IsTrue(cut.Markup.Contains("OPERATIONAL"));
        Assert.IsTrue(cut.Markup.Contains("14 of 15 services healthy"));
        // Telemetry strip chips present.
        Assert.IsTrue(cut.Markup.Contains("99.94%"));
        Assert.IsTrue(cut.Markup.Contains("142 ms"));
        Assert.IsTrue(cut.Markup.Contains("387 ms"));
        // The degraded-count chip (degraded:1) must carry the degraded tone
        // class — regression guard for HudChip's tone vocabulary (Hermes #118).
        cut.Find(".telemetry-strip .chip .v.degraded");
    }

    [TestMethod]
    public void Down_EscalatesToCriticalHero()
    {
        using var ctx = CtxWithSummaryJson(DownJson);
        var cut = ctx.RenderComponent<HudDashboardHero>();

        cut.WaitForAssertion(() => cut.Find(".panel.critical"));
        Assert.IsTrue(cut.Markup.Contains("CRITICAL"));
        Assert.IsTrue(cut.Markup.Contains("2 of 15 services down"));
    }

    [TestMethod]
    public void Empty_RendersHonestZeroState_NotCrash()
    {
        using var ctx = CtxWithSummaryJson(EmptyJson);
        var cut = ctx.RenderComponent<HudDashboardHero>();

        cut.WaitForAssertion(() => cut.Find(".panel.primary"));
        Assert.IsTrue(cut.Markup.Contains("0 of 0 services healthy"));
        // Null latency renders as em dash, not "0 ms" or a crash.
        Assert.IsTrue(cut.Markup.Contains("—"));
    }

    [TestMethod]
    public void SummaryUnavailable_RendersCalmTelemetryUnavailable()
    {
        // Transport failure → API client returns null → calm fallback.
        using var ctx = CtxWithSummaryJson(null, HttpStatusCode.InternalServerError);
        var cut = ctx.RenderComponent<HudDashboardHero>();

        cut.WaitForAssertion(() =>
            Assert.IsTrue(cut.Markup.Contains("TELEMETRY") && cut.Markup.Contains("Unavailable")));
        // Must not throw / must not claim a fake state.
        Assert.AreEqual(0, cut.FindAll(".panel.critical").Count);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string? _json;
        private readonly HttpStatusCode _code;
        public StubHandler(string? json, HttpStatusCode code) { _json = json; _code = code; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(_code);
            if (_json is not null)
            {
                resp.Content = new StringContent(_json, Encoding.UTF8, "application/json");
            }
            return Task.FromResult(resp);
        }
    }
}
