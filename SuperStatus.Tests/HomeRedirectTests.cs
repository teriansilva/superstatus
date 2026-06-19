using System.Net;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #184 — the public root redirects into first-run setup on a fresh
/// (not-onboarded) install, but only on a CONFIRMED not-onboarded response: an
/// unreachable /settings must NOT bounce public visitors to login.
/// </summary>
[TestClass]
public class HomeRedirectTests
{
    private sealed class Stub(HttpStatusCode settingsCode, string settingsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            (HttpStatusCode code, string json) = path switch
            {
                "/settings"            => (settingsCode, settingsJson),
                "/statuscheck"         => (HttpStatusCode.OK, """{"results":[],"totalCount":0,"page":1,"pageSize":50}"""),
                "/statuscheck/historical-overview" => (HttpStatusCode.OK, "[]"),  // #226: dashboard's batched strip fetch
                "/statuscheck/summary" => (HttpStatusCode.OK, """{"services":{"up":0,"degraded":0,"down":0,"total":0},"latency_ms":{"avg":0,"p95":0},"uptime_30d_pct":100,"incidents_30d":0,"per_service":[],"overall":"up","generated_utc":"2026-01-01T00:00:00Z"}"""),
                "/incidents"           => (HttpStatusCode.OK, "{}"),
                _                      => (HttpStatusCode.OK, "{}"),
            };
            var resp = new HttpResponseMessage(code);
            if (!string.IsNullOrEmpty(json)) resp.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }

    private static (BunitTestContext ctx, FakeNavigationManager nav) Ctx(HttpStatusCode settingsCode, string settingsJson)
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization(); // anonymous
        ctx.Services.AddMudServices();
        var http = new HttpClient(new Stub(settingsCode, settingsJson)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new SettingsApiClient(http));
        ctx.Services.AddSingleton(new StatusApiClient(http));
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var nav = ctx.Services.GetRequiredService<FakeNavigationManager>();
        return (ctx, nav);
    }

    [TestMethod]
    public void NotOnboarded_RedirectsToAdmin()
    {
        var (ctx, nav) = Ctx(HttpStatusCode.OK, "{}"); // OnboardedUtc absent → not onboarded
        using var _ = ctx;
        var cut = ctx.RenderComponent<Home>();

        StringAssert.EndsWith(nav.Uri, "/admin");
        Assert.AreEqual(0, cut.FindAll(".home-section").Count, "dashboard is not shown while redirecting to setup");
    }

    [TestMethod]
    public void Onboarded_RendersDashboard_NoRedirect()
    {
        var (ctx, nav) = Ctx(HttpStatusCode.OK, """{"onboardedUtc":"2026-01-01T00:00:00Z"}""");
        using var _ = ctx;
        var cut = ctx.RenderComponent<Home>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.FindAll(".home-section").Count > 0, "dashboard renders once onboarded"));
        Assert.IsFalse(nav.Uri.EndsWith("/admin"), "no redirect when onboarded");
    }

    [TestMethod]
    public void SettingsUnavailable_RendersDashboard_NoRedirect()
    {
        var (ctx, nav) = Ctx(HttpStatusCode.InternalServerError, ""); // /settings down → TryGet returns null
        using var _ = ctx;
        var cut = ctx.RenderComponent<Home>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.FindAll(".home-section").Count > 0,
            "a failed settings fetch must not bounce public visitors to login"));
        Assert.IsFalse(nav.Uri.EndsWith("/admin"), "no redirect when settings are unavailable");
    }
}
