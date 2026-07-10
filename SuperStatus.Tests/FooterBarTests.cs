using Bunit;
using Bunit.TestDoubles;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web.Components.Layout;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #170 — customizable footer. FooterBar renders the cascaded settings'
/// static text + links and honours the ShowAdminLink toggle.
/// </summary>
[TestClass]
public class FooterBarTests
{
    private static BunitTestContext Ctx(bool authenticated = false)
    {
        var ctx = new BunitTestContext();
        var auth = ctx.AddTestAuthorization();
        if (authenticated) auth.SetAuthorized("operator");
        return ctx;
    }

    private static SiteSettingsViewModel Settings(bool showAdmin) => new()
    {
        FooterText = "© 2026 Acme — operational status",
        FooterLinks = new() { new FooterLink { Label = "Privacy", Url = "https://www.example.com/privacy" } },
        ShowAdminLink = showAdmin,
    };

    [TestMethod]
    public void RendersConfiguredTextAndLinks_WithAdminLink()
    {
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<FooterBar>(p => p.AddCascadingValue("SiteSettings", Settings(showAdmin: true)));

        Assert.IsTrue(cut.Find(".classification").TextContent.Contains("© 2026 Acme"));
        var privacy = cut.Find(".footer-links a[href='https://www.example.com/privacy']");
        Assert.AreEqual("Privacy", privacy.TextContent.Trim());
        Assert.AreEqual("noopener", privacy.GetAttribute("rel"), "external footer links open safely");
        Assert.IsNotNull(cut.Find(".footer-links a[href='/admin']"), "admin link shows when toggled on");
        // No rotating ambience component.
        Assert.AreEqual(0, cut.FindAll(".hud-classification").Count);
    }

    [TestMethod]
    public void AdminLinkHidden_WhenToggleOff()
    {
        using var ctx = Ctx();
        var cut = ctx.RenderComponent<FooterBar>(p => p.AddCascadingValue("SiteSettings", Settings(showAdmin: false)));

        Assert.AreEqual(0, cut.FindAll(".footer-links a[href='/admin']").Count,
            "admin link hidden when ShowAdminLink is off");
        // The configured text + other links still render.
        Assert.IsTrue(cut.Find(".classification").TextContent.Contains("© 2026 Acme"));
    }

    [TestMethod]
    public void Authenticated_ShowsOperatorConsoleAndSignOut()
    {
        using var ctx = Ctx(authenticated: true);
        var cut = ctx.RenderComponent<FooterBar>(p => p.AddCascadingValue("SiteSettings", Settings(showAdmin: true)));

        var links = cut.Find(".footer-links").TextContent;
        Assert.IsTrue(links.Contains("Operator console"), "authed admin entry reads 'Operator console'");
        Assert.IsTrue(links.Contains("Sign out"));
    }
}
