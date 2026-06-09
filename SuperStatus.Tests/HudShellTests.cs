using Bunit;
using Bunit.TestDoubles;
using SuperStatus.Web.Components.Layout;
using SuperStatus.Web.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #95 Phase 2 — tactical-HUD shell. Verifies the reskinned
/// HeaderBar / FooterBar / Error surfaces compose Hud primitives and
/// honor the auth-gated rendering rules.
/// </summary>
[TestClass]
public class HudShellTests
{
    private static BunitTestContext CreateContextWith(bool authenticated)
    {
        var ctx = new BunitTestContext();
        var auth = ctx.AddTestAuthorization();
        if (authenticated)
        {
            auth.SetAuthorized("operator");
        }
        // #177: HeaderBar hosts ThemeToggle, which calls hudTheme.get via JS on
        // first render — Loose so the unplanned invocation returns a default.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [TestMethod]
    public void HeaderBar_RendersBrand_TagWithLed_AndClock()
    {
        using var ctx = CreateContextWith(authenticated: false);
        var cut = ctx.RenderComponent<HeaderBar>();

        cut.Find(".hud-topbar");
        cut.Find(".brand .name");
        // STATUS // STATE tag with pulsing LED — accent toned and Up by default.
        cut.Find(".tag.accent .led.up");
        // Clock cell is present (text format checked elsewhere).
        cut.Find(".clock");
    }

    [TestMethod]
    public void HeaderBar_DegradedState_DrivesAmberLedAndUppercaseLabel()
    {
        using var ctx = CreateContextWith(authenticated: false);
        var cut = ctx.RenderComponent<HeaderBar>(p => p
            .Add(x => x.SystemState, "degraded"));

        cut.Find(".tag.accent .led.degraded");
        Assert.IsTrue(cut.Markup.Contains("DEGRADED"));
    }

    [TestMethod]
    public void HeaderBar_NotAuthenticated_ShowsNoAuthButtons()
    {
        using var ctx = CreateContextWith(authenticated: false);
        var cut = ctx.RenderComponent<HeaderBar>();

        // #170: the public sign-in button was removed; anonymous users get no
        // auth-driven topbar control (console entry is the footer Admin link).
        var actions = cut.Find(".topbar-actions");
        Assert.IsTrue(actions.QuerySelector("a[href='/login?returnUrl=/']") is null,
            "Sign-in button must no longer render for anonymous users (#170).");
        Assert.IsTrue(actions.QuerySelector("a[href='/admin']") is null,
            "Admin button must not render for anonymous users.");
        Assert.IsTrue(actions.QuerySelector("a[href='/logout']") is null,
            "Sign-out button must not render for anonymous users.");
    }

    [TestMethod]
    public void HeaderBar_Authenticated_NoAdminGear_NoGridIcon_NoLogoutOrLogin()
    {
        using var ctx = CreateContextWith(authenticated: true);
        var cut = ctx.RenderComponent<HeaderBar>();

        var actions = cut.Find(".topbar-actions");
        // The topbar admin gear was removed — the console entry is the
        // footer Admin link.
        Assert.IsTrue(actions.QuerySelector("a[href='/admin']") is null,
            "admin gear no longer renders in the topbar — use the footer Admin link");
        // #175: the topbar logout icon was removed — Sign out lives in the footer.
        Assert.IsTrue(actions.QuerySelector("a[href='/logout']") is null,
            "Sign-out icon must no longer render in the topbar (#175).");
        Assert.IsTrue(actions.QuerySelector("a[href='/login?returnUrl=/']") is null,
            "Sign-in button must not render once authenticated.");
        // The theme toggle remains (the only remaining topbar action).
        Assert.IsTrue(actions.Children.Length >= 1, "the theme toggle remains in the topbar");
    }

    [TestMethod]
    public void FooterBar_RendersStaticClassificationAndPoweredLink()
    {
        using var ctx = CreateContextWith(authenticated: false);
        // #170: the footer is now a static line (no rotating ambience). With no
        // settings cascade it falls back to the seeded classification text.
        var cut = ctx.RenderComponent<FooterBar>();

        cut.Find(".hud-footer");
        var classification = cut.Find(".classification");
        Assert.IsTrue(classification.TextContent.Contains("UNCLASSIFIED"));
        Assert.IsTrue(classification.TextContent.Contains("INTERNAL USE"));
        Assert.AreEqual(0, cut.FindAll(".hud-classification").Count,
            "The #109 rotating classification component must be gone (#170).");
        Assert.IsTrue(cut.Find(".powered").GetAttribute("href")
            == "https://superstatus.io");
    }

    [TestMethod]
    public void FooterBar_Authenticated_ShowsSignOutLink()
    {
        using var ctx = CreateContextWith(authenticated: true);
        var cut = ctx.RenderComponent<FooterBar>();

        Assert.IsTrue(cut.Find(".footer-links").TextContent.Contains("Sign out"));
    }

    [TestMethod]
    public void Error_RendersCriticalHudPanelWithReturnLink()
    {
        using var ctx = CreateContextWith(authenticated: false);
        var cut = ctx.RenderComponent<Error>();

        cut.Find(".panel.critical");
        Assert.AreEqual(4, cut.FindAll(".cnr").Count);
        var returnLink = cut.Find("a.btn.primary");
        Assert.AreEqual("/", returnLink.GetAttribute("href"));
        Assert.IsTrue(returnLink.TextContent.Contains("RETURN"));
    }

}
