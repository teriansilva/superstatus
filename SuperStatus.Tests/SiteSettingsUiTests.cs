using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Layout;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #167 Phase 2 — operator branding in the shell. Verifies the
/// SiteSettingsProvider applies the chosen accent on :root (and only for a valid
/// #rrggbb), and that HeaderBar honours the cascaded title + logo.
/// </summary>
[TestClass]
public class SiteSettingsUiTests
{
    // Stub the /settings GET so the provider renders without a real backend.
    private sealed class StubHandler(SiteSettingsViewModel vm) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(vm, Json), Encoding.UTF8, "application/json"),
            });
    }

    private static BunitTestContext ContextWithSettings(SiteSettingsViewModel vm)
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        var client = new SettingsApiClient(new HttpClient(new StubHandler(vm)) { BaseAddress = new Uri("http://demo.local") });
        ctx.Services.AddSingleton(client);
        return ctx;
    }

    [TestMethod]
    public void Provider_AppliesAccentVars_ForValidHex()
    {
        using var ctx = ContextWithSettings(new SiteSettingsViewModel { AccentColor = "#f5a524" });
        var cut = ctx.RenderComponent<SiteSettingsProvider>();

        // The :root override carries the accent + its rgba soft/glow/bg derivations.
        Assert.IsTrue(cut.Markup.Contains("--accent:#f5a524"), "accent var applied");
        Assert.IsTrue(cut.Markup.Contains("--accent-soft:rgba(245,165,36,0.55)"), "accent-soft derived from hex");
        Assert.IsTrue(cut.Markup.Contains("--accent-glow:rgba(245,165,36,0.20)"), "accent-glow derived from hex");
        // #324: the atmospheric body wash now tracks the accent too.
        Assert.IsTrue(cut.Markup.Contains("--accent-bg:rgba(245,165,36,0.06)"), "accent-bg (canvas wash) derived from hex");
    }

    [TestMethod]
    public void Provider_AccentBg_ForDefaultAccent_ReproducesHudThemeGreenWash()
    {
        // #324 invariant: with the default accent (#3fbf6f), the emitted --accent-bg
        // must equal the hardcoded green wash in hud-theme.css (rgba(63,191,111,0.06)),
        // so the canvas looks identical to before for operators who never recolour.
        using var ctx = ContextWithSettings(new SiteSettingsViewModel { AccentColor = "#3fbf6f" });
        var cut = ctx.RenderComponent<SiteSettingsProvider>();

        Assert.IsTrue(cut.Markup.Contains("--accent-bg:rgba(63,191,111,0.06)"),
            "default accent reproduces the existing hud-theme.css green canvas wash — no visual change when unrecoloured");
    }

    [TestMethod]
    public void Provider_OmitsStyle_ForInvalidOrEmptyAccent()
    {
        using var ctx = ContextWithSettings(new SiteSettingsViewModel { AccentColor = "" });
        var cut = ctx.RenderComponent<SiteSettingsProvider>();

        Assert.IsFalse(cut.Markup.Contains(":root{--accent"),
            "no accent override emitted for an empty/invalid accent — theme default stands");
    }

    [TestMethod]
    public void HeaderBar_CustomTitle_ReplacesStylizedWordmark()
    {
        using var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // #177: HeaderBar→ThemeToggle calls JS on render
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .AddCascadingValue("SiteSettings", new SiteSettingsViewModel { Title = "Acme Service Status" }));

        var name = cut.Find(".brand .name");
        Assert.AreEqual("Acme Service Status", name.TextContent.Trim());
        Assert.IsNull(name.QuerySelector("em"), "custom title is plain text, not the SUPER//STATUS mark");
    }

    [TestMethod]
    public void HeaderBar_DefaultTitle_KeepsStylizedWordmark()
    {
        using var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // #177: HeaderBar→ThemeToggle calls JS on render
        // "SuperStatus" (the ultimate seed fallback) keeps the stylized mark.
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .AddCascadingValue("SiteSettings", new SiteSettingsViewModel { Title = "SuperStatus" }));

        Assert.IsNotNull(cut.Find(".brand .name em"), "stylized SUPER<em>STATUS</em> retained for the default title");
    }

    [TestMethod]
    public void HeaderBar_CascadedSubtitle_RendersBrandDesc()
    {
        using var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // #177: HeaderBar→ThemeToggle calls JS on render
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .AddCascadingValue("SiteSettings", new SiteSettingsViewModel
            {
                Title = "TEST",
                Subtitle = "Acme Service Status Information",
            }));

        Assert.AreEqual("Acme Service Status Information", cut.Find(".brand-desc").TextContent.Trim());
    }

    [TestMethod]
    public void HeaderBar_CascadedLogo_RendersBrandImage()
    {
        using var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // #177: HeaderBar→ThemeToggle calls JS on render
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .AddCascadingValue("SiteSettings", new SiteSettingsViewModel
            {
                Title = "Acme Service Status",
                LogoUrl = "https://cdn.example.com/logo.svg",
            }));

        var img = cut.Find("img.brand-logo");
        Assert.AreEqual("https://cdn.example.com/logo.svg", img.GetAttribute("src"));
    }

    // ---- #286: no defaults — operator-set values only, no green box, no STATUS tag ----

    private static BunitTestContext HeaderContext()
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // HeaderBar→ThemeToggle calls JS on render
        return ctx;
    }

    [TestMethod]
    public void HeaderBar_BlankTitle_RendersNoWordmark()
    {
        using var ctx = HeaderContext();
        // Operator cleared the title (e.g. their logo carries the name) — show nothing.
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .AddCascadingValue("SiteSettings", new SiteSettingsViewModel { Title = "" }));

        Assert.AreEqual(0, cut.FindAll(".brand .name").Count,
            "#286: a blank title renders no wordmark (no default SUPER//STATUS fallback).");
    }

    [TestMethod]
    public void HeaderBar_BlankSubtitle_RendersNoBrandDesc()
    {
        using var ctx = HeaderContext();
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .AddCascadingValue("SiteSettings", new SiteSettingsViewModel { Title = "Acme", Subtitle = "" }));

        Assert.AreEqual(0, cut.FindAll(".brand-desc").Count,
            "#286: a blank subtitle renders nothing (no SuperStatusConfig.Description fallback).");
    }

    [TestMethod]
    public void HeaderBar_NoLogoSet_RendersNoImageOrGlyph()
    {
        using var ctx = HeaderContext();
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .AddCascadingValue("SiteSettings", new SiteSettingsViewModel { Title = "Acme", LogoUrl = "" }));

        Assert.AreEqual(0, cut.FindAll("img.brand-logo").Count,
            "#286: no logo image when the operator hasn't set one (no config-default logo).");
        Assert.AreEqual(0, cut.FindAll(".brand .logo").Count,
            "#286: the green-box placeholder glyph was removed entirely.");
    }

    [TestMethod]
    public void HeaderBar_OmitsStatusTag()
    {
        using var ctx = HeaderContext();
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .AddCascadingValue("SiteSettings", new SiteSettingsViewModel { Title = "SuperStatus" }));

        Assert.AreEqual(0, cut.FindAll(".topbar-status").Count,
            "#286: the hardcoded STATUS//UP gimmick tag was removed.");
    }
}
