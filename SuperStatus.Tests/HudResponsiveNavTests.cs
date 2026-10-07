using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using SuperStatus.Web.Components.Layout;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #152 Phase 1 — mobile nav drawer + responsive topbar. Verifies the
/// HeaderBar hamburger's a11y wiring + toggle callback and the NavMenu drawer
/// contract (the aria-controls target id, and close-on-navigate). The visual
/// breakpoint behavior itself is proved by the web/visual pipeline; these lock
/// the interaction contract that drives it.
/// </summary>
[TestClass]
public class HudResponsiveNavTests
{
    private static BunitTestContext NewContext()
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        // NavMenu injects IWebHostEnvironment (Env.IsDevelopment() gates the
        // mockup-gallery link). Default to non-Development.
        ctx.Services.AddSingleton<IWebHostEnvironment>(new StubWebHostEnvironment("Production"));
        // #177: HeaderBar hosts ThemeToggle, which calls hudTheme.get via JS.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [TestMethod]
    public void HeaderBar_Hamburger_HasMenuAriaWiring()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<HeaderBar>();

        var btn = cut.Find(".hud-menu-btn");
        // Collapsed by default → aria-expanded=false; controls the nav by id.
        Assert.AreEqual("false", btn.GetAttribute("aria-expanded"));
        Assert.AreEqual("primary-nav", btn.GetAttribute("aria-controls"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(btn.GetAttribute("aria-label")),
            "Hamburger needs an accessible label (it's an icon-only button).");
    }

    [TestMethod]
    public void HeaderBar_Hamburger_ReflectsOpenStateInAriaExpanded()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<HeaderBar>(p => p.Add(x => x.NavOpen, true));

        Assert.AreEqual("true", cut.Find(".hud-menu-btn").GetAttribute("aria-expanded"));
    }

    [TestMethod]
    public void HeaderBar_Hamburger_Click_InvokesOnMenuToggle()
    {
        using var ctx = NewContext();
        var toggled = 0;
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .Add(x => x.OnMenuToggle, EventCallback.Factory.Create(this, () => toggled++)));

        cut.Find(".hud-menu-btn").Click();

        Assert.AreEqual(1, toggled, "Clicking the hamburger should raise OnMenuToggle.");
    }

    [TestMethod]
    public void NavMenu_ExposesPrimaryNavTarget_MatchingHamburgerAriaControls()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<NavMenu>();

        // The aria-controls target the hamburger points at must exist.
        var nav = cut.Find("nav#primary-nav");
        Assert.AreEqual("Primary", nav.GetAttribute("aria-label"));
    }

    [TestMethod]
    public void NavMenu_NavLinkClick_InvokesOnNavigate()
    {
        using var ctx = NewContext();
        var navigated = 0;
        var cut = ctx.RenderComponent<NavMenu>(p => p
            .Add(x => x.OnNavigate, EventCallback.Factory.Create(this, () => navigated++)));

        // Clicking a destination closes the mobile drawer (no-op on desktop).
        cut.Find("a.nav-link").Click();

        Assert.AreEqual(1, navigated, "A nav-link click should raise OnNavigate.");
    }

    /// <summary>Minimal IWebHostEnvironment for bUnit DI (NavMenu's Env injection).</summary>
    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string environmentName) => EnvironmentName = environmentName;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "SuperStatus.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
