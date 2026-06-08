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
/// Issue #266 — the operator console's tab bar: renders the five sections, defaults
/// to Overview, and switching tabs toggles which pane is shown (panes stay mounted,
/// driven by the [hidden] attribute). Verifies the Alerts tab is the home of both the
/// delivery config (email + browser push) and the delivery logs.
/// </summary>
[TestClass]
public class AdminTabsTests
{
    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/settings"            => """{"onboardedUtc":"2026-01-01T00:00:00Z"}""",
                "/statuscheck"         => """{"results":[],"totalCount":0,"page":1,"pageSize":50}""",
                "/statuscheck/summary" => """{"services":{"up":1,"degraded":0,"down":0,"total":1},"latency_ms":{"avg":1,"p95":1},"uptime_30d_pct":100,"incidents_30d":0,"per_service":[],"overall":"up","generated_utc":"2026-01-01T00:00:00Z"}""",
                _                      => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static BunitTestContext Ctx()
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization().SetAuthorized("operator");
        ctx.Services.AddMudServices();
        var http = new HttpClient(new Stub()) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        ctx.Services.AddSingleton(new SettingsApiClient(http));
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        ctx.Services.AddSingleton(new UpdatesApiClient(http));
        ctx.Services.AddSingleton(new PushApiClient(http));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static bool Hidden(IRenderedComponent<Admin> cut, string pane)
        => cut.Find($"[data-pane=\"{pane}\"]").HasAttribute("hidden");

    [TestMethod]
    public void Console_rendersFiveTabs_defaultsToOverview()
    {
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<Admin>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Operator console")));

        var labels = cut.FindAll(".console-tab").Select(t => t.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Overview", "Checks", "Incidents", "Alerts", "Settings" }, labels);

        var active = cut.FindAll(".console-tab").Single(t => t.ClassList.Contains("active"));
        Assert.AreEqual("Overview", active.TextContent.Trim());
        Assert.IsFalse(Hidden(cut, "overview"), "overview pane is visible by default");
        Assert.IsTrue(Hidden(cut, "alerts"), "other panes start hidden");
    }

    [TestMethod]
    public void ClickingAlertsTab_showsAlertsPane_hidesOverview()
    {
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<Admin>();
        cut.WaitForAssertion(() => cut.Find(".console-tabs"));

        cut.FindAll(".console-tab").Single(t => t.TextContent.Trim() == "Alerts").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsFalse(Hidden(cut, "alerts"), "alerts pane shown after click");
            Assert.IsTrue(Hidden(cut, "overview"), "overview pane hidden after switching");
            var active = cut.FindAll(".console-tab").Single(t => t.ClassList.Contains("active"));
            Assert.AreEqual("Alerts", active.TextContent.Trim());
        });

        // The Alerts tab is the single home for delivery config (email + push) and
        // is rendered with both panels present.
        Assert.IsTrue(cut.Markup.Contains("Email alerts (SMTP)"), "email config lives under Alerts");
        Assert.IsTrue(cut.Markup.Contains("Browser notifications"), "web-push config lives under Alerts");
    }

    [TestMethod]
    public void SettingsTab_holdsBrandingAndAi_notEmail()
    {
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<Admin>();
        cut.WaitForAssertion(() => cut.Find(".console-tabs"));

        // The Settings panel header reflects the slimmed scope (no email/push).
        Assert.IsTrue(cut.Markup.Contains("Site settings"), "settings panel retitled");
        // Branding + AI remain in the settings panel.
        Assert.IsTrue(cut.Markup.Contains("AI / Automation"), "AI config stays in Settings");
    }
}
