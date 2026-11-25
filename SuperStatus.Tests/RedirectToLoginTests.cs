using Bunit;
using Bunit.TestDoubles;
using SuperStatus.Web.Components.Layout;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #181 — anonymous hits on an [Authorize] route are bounced to the Web
/// /login endpoint (which issues the OIDC challenge). RedirectToLogin is what
/// AuthorizeRouteView's NotAuthorized renders; it must preserve the target as a
/// returnUrl so the IdP returns the user where they were headed.
/// </summary>
[TestClass]
public class RedirectToLoginTests
{
    [TestMethod]
    public void RedirectsToLoginEndpoint_PreservingReturnUrl()
    {
        using var ctx = new BunitTestContext();
        var nav = ctx.Services.GetRequiredService<FakeNavigationManager>();
        nav.NavigateTo("admin"); // the protected route the anonymous user requested

        ctx.RenderComponent<RedirectToLogin>();

        StringAssert.Contains(nav.Uri, "/login?returnUrl=");
        // returnUrl is the escaped absolute-path of the requested page.
        StringAssert.Contains(nav.Uri, Uri.EscapeDataString("/admin"));
    }

    [TestMethod]
    public void Root_RedirectsToLoginWithRootReturnUrl()
    {
        using var ctx = new BunitTestContext();
        var nav = ctx.Services.GetRequiredService<FakeNavigationManager>();

        ctx.RenderComponent<RedirectToLogin>();

        StringAssert.Contains(nav.Uri, "/login?returnUrl=" + Uri.EscapeDataString("/"));
    }
}
