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
/// Issue #266 — the operator console's tab bar: renders the sections, defaults
/// to Overview, and switching tabs toggles which pane is shown (panes stay mounted,
/// driven by the [hidden] attribute). Verifies the Alerts tab is the home of the
/// delivery config (email + browser push) and the alert log; #291 Phase B adds the
/// Webhooks tab (between Incidents and Alerts) which now owns the webhook log.
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
                "/admin/webhooks"      => "[]",
                "/admin/webhook-log"   => "[]",
                "/admin/alert-log"     => "[]",
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
        ctx.Services.AddSingleton(new IssuerModeInfo(isDynamic: false)); // #358: pinned mode ⇒ no banner/editor (markup unchanged)
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        ctx.Services.AddSingleton(new UpdatesApiClient(http));
        ctx.Services.AddSingleton(new PushApiClient(http));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static bool Hidden(IRenderedComponent<Admin> cut, string pane)
        => cut.Find($"[data-pane=\"{pane}\"]").HasAttribute("hidden");

    [TestMethod]
    public void Console_rendersSevenTabs_SlasBetweenChecksAndIncidents_defaultsToOverview()
    {
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<Admin>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Operator console")));

        // #302: SLAs promoted to its own tab, between Checks and Incidents.
        // #319: About is the last tab (Support + about info).
        // #343 Phase 4: the standalone Webhooks tab is folded out (webhooks are a
        // notification channel on an alert profile now).
        var labels = cut.FindAll(".console-tab").Select(t => t.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Overview", "Checks", "SLAs", "Incidents", "Alerts", "Settings", "About" }, labels);

        var active = cut.FindAll(".console-tab").Single(t => t.ClassList.Contains("active"));
        Assert.AreEqual("Overview", active.TextContent.Trim());
        Assert.IsFalse(Hidden(cut, "overview"), "overview pane is visible by default");
        Assert.IsTrue(Hidden(cut, "alerts"), "other panes start hidden");
    }

    [TestMethod]
    public void AboutPane_showsSupportAndAboutInfo_withCoffeeLink()
    {
        // #319: the About tab carries a Buy-me-a-coffee support link plus the
        // app's version / source / attribution.
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<Admin>();
        cut.WaitForAssertion(() => cut.Find(".console-tabs"));

        var aboutPane = cut.Find("[data-pane=\"about\"]").InnerHtml;
        StringAssert.Contains(aboutPane, "buymeacoffee.com/teriansilva", "Support block links to Buy-me-a-coffee");
        StringAssert.Contains(aboutPane, "github.com/teriansilva/superstatus", "About block links to the source");
        StringAssert.Contains(aboutPane, "Marcus Braun", "About block credits the author");

        // Clicking the tab reveals its pane and hides Overview.
        cut.FindAll(".console-tab").Single(t => t.TextContent.Trim() == "About").Click();
        cut.WaitForAssertion(() => Assert.IsFalse(Hidden(cut, "about"), "about pane shown after click"));
        Assert.IsTrue(Hidden(cut, "overview"), "overview pane hidden after switching");
    }

    [TestMethod]
    public void SlasPane_ownsSlaPanel_ChecksPaneNoLongerHasIt()
    {
        // #302: the SLA management panel moved from under the Checks list to its
        // own SLAs pane.
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<Admin>();
        cut.WaitForAssertion(() => cut.Find(".console-tabs"));

        var slasPane = cut.Find("[data-pane=\"slas\"]").InnerHtml;
        Assert.IsTrue(slasPane.Contains("Service-level definitions"), "SLA panel lives on the SLAs pane");

        var checksPane = cut.Find("[data-pane=\"checks\"]").InnerHtml;
        Assert.IsFalse(checksPane.Contains("Service-level definitions"), "SLA panel no longer under the Checks list");

        cut.FindAll(".console-tab").Single(t => t.TextContent.Trim() == "SLAs").Click();
        cut.WaitForAssertion(() => Assert.IsFalse(Hidden(cut, "slas"), "SLAs pane shown after click"));
        Assert.IsTrue(Hidden(cut, "checks"), "checks pane hidden");
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
    public void WebhooksTab_isFoldedOut_NotInTheNav()
    {
        // #343 Phase 4: webhooks are folded into the notification-channel model, so the
        // standalone Webhooks tab is removed from the console nav — webhooks are managed
        // as a `webhook` channel on an alert profile, and their deliveries appear in the
        // unified alert log on the Alerts pane.
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<Admin>();
        cut.WaitForAssertion(() => cut.Find(".console-tabs"));

        var labels = cut.FindAll(".console-tab").Select(t => t.TextContent.Trim()).ToList();
        Assert.IsFalse(labels.Contains("Webhooks"), "the standalone Webhooks tab is folded out of the nav");

        var alertsPane = cut.Find("[data-pane=\"alerts\"]").InnerHtml;
        Assert.IsTrue(alertsPane.Contains("ALERT LOG"), "the unified alert log stays on the Alerts pane");
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
