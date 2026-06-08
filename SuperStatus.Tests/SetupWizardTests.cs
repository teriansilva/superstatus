using System.Net;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using SuperStatus.Web.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #181 Phase 3 — the admin console renders the first-run setup wizard
/// inline while the site is not onboarded (SiteSettings.OnboardedUtc == null),
/// and the wizard itself starts on the Welcome step.
/// </summary>
[TestClass]
public class SetupWizardTests
{
    // /settings returns the given onboarded state; everything else returns a
    // benign empty payload so neither the console nor the wizard crashes.
    private sealed class Stub(string settingsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/settings"            => settingsJson,
                "/statuscheck"         => """{"results":[],"totalCount":0,"page":1,"pageSize":50}""",
                "/statuscheck/summary" => """{"services":{"up":1,"degraded":0,"down":0,"total":1},"latency_ms":{"avg":1,"p95":1},"uptime_30d_pct":100,"incidents_30d":0,"per_service":[],"overall":"up","generated_utc":"2026-01-01T00:00:00Z"}""",
                "/incidents"           => "{}",
                _                      => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static BunitTestContext Ctx(string settingsJson)
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization().SetAuthorized("operator");
        ctx.Services.AddMudServices();
        var http = new HttpClient(new Stub(settingsJson)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        ctx.Services.AddSingleton(new SettingsApiClient(http));
        ctx.Services.AddSingleton(new IncidentApiClient(http)); // console renders IncidentList
        ctx.Services.AddSingleton(new UpdatesApiClient(http));  // #249: console renders UpdatesPanel
        ctx.Services.AddSingleton(new PushApiClient(http));     // #241 Phase C: wizard/console host EnablePushButton
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [TestMethod]
    public void Admin_NotOnboarded_RendersWizard()
    {
        using var ctx = Ctx("{}"); // OnboardedUtc absent → not onboarded
        var cut = ctx.RenderComponent<Admin>();

        cut.WaitForAssertion(() => cut.Find(".setup-wizard"));
        Assert.IsTrue(cut.Markup.Contains("Welcome"), "wizard starts on the Welcome step");
        Assert.IsTrue(cut.Markup.Contains("Get started"));
        Assert.IsFalse(cut.Markup.Contains("Operator overview"), "the console is not shown until onboarded");
    }

    [TestMethod]
    public void Admin_Onboarded_RendersConsoleNotWizard()
    {
        using var ctx = Ctx("""{"onboardedUtc":"2026-01-01T00:00:00Z"}""");
        var cut = ctx.RenderComponent<Admin>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Operator overview")));
        Assert.AreEqual(0, cut.FindAll(".setup-wizard").Count, "no wizard once onboarded");
    }

    [TestMethod]
    public void Wizard_StartsOnWelcomeStep()
    {
        using var ctx = Ctx("{}");
        var cut = ctx.RenderComponent<SetupWizard>();

        cut.WaitForAssertion(() => cut.Find(".setup-intro"));
        Assert.IsTrue(cut.Markup.Contains("Step 1 of 6")); // #168 AI + #241 email-alerts + notifications steps
        Assert.IsTrue(cut.Find("button.btn.primary").TextContent.Contains("Get started"));
    }
}
