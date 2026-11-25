using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using MudBlazor.Services;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Layout;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #169 — the public shell drops the nav rail/drawer; the operator console
/// keeps it. Renders both layouts with a trivial body and asserts the nav
/// chrome is present only on MainLayout.
/// </summary>
[TestClass]
public class PublicLayoutTests
{
    private sealed class SettingsStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new SiteSettingsViewModel(), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8, "application/json"),
            });
    }

    // NavMenu (rendered by MainLayout) injects IWebHostEnvironment to gate its
    // Development-only mockup-gallery section.
    private sealed class FakeEnv : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SuperStatus.Web";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static BunitTestContext ShellContext()
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(new SettingsApiClient(new HttpClient(new SettingsStub()) { BaseAddress = new Uri("http://demo.local") }));
        ctx.Services.AddSingleton<IWebHostEnvironment>(new FakeEnv());
        // #377: both layouts now host DemoBanner. Off here, so these shell assertions
        // keep describing a normal deployment; DemoBannerTests covers the on state.
        ctx.Services.AddSingleton(new SuperStatus.Web.DemoModeInfo(isEnabled: false));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static readonly RenderFragment Body = b => b.AddMarkupContent(0, "<p id=\"probe\">body</p>");

    [TestMethod]
    public void PublicLayout_HasNoNavRailOrHamburger()
    {
        using var ctx = ShellContext();
        var cut = ctx.RenderComponent<PublicLayout>(p => p.Add(x => x.Body, Body));

        cut.Find(".app.app-public");        // single-column public shell
        cut.Find(".hud-topbar");            // topbar still present
        cut.Find(".hud-footer");            // footer still present
        cut.Find("#probe");                 // page body rendered

        Assert.AreEqual(0, cut.FindAll(".nav").Count, "public shell must not render the nav rail");
        Assert.AreEqual(0, cut.FindAll(".hud-menu-btn").Count, "public shell must not render the nav hamburger");
    }

    [TestMethod]
    public void MainLayout_KeepsNavRailAndHamburger()
    {
        using var ctx = ShellContext();
        var cut = ctx.RenderComponent<MainLayout>(p => p.Add(x => x.Body, Body));

        Assert.IsTrue(cut.FindAll(".nav").Count > 0, "operator console keeps the nav rail");
        Assert.IsTrue(cut.FindAll(".hud-menu-btn").Count > 0, "operator console keeps the nav hamburger");
        Assert.AreEqual(0, cut.FindAll(".app-public").Count, "console is not the public single-column shell");
    }
}
