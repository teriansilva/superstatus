using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Web;
using SuperStatus.Web.Components.Layout;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #377 — the site-wide demo banner renders only on the demo instance, and the
/// demo chrome never borrows the semantic status tokens that mean "a service is
/// unhealthy".
/// </summary>
[TestClass]
public class DemoBannerTests
{
    private static BunitTestContext Ctx(bool demoEnabled)
    {
        var ctx = new BunitTestContext();
        ctx.Services.AddSingleton(new DemoModeInfo(demoEnabled));
        return ctx;
    }

    [TestMethod]
    public void WhenDemoEnabled_RendersBannerWithCredentialsAndCountdown()
    {
        using var ctx = Ctx(demoEnabled: true);

        var cut = ctx.RenderComponent<DemoBanner>();

        var banner = cut.Find(".demo-banner");
        StringAssert.Contains(banner.TextContent, "Public demo");
        StringAssert.Contains(banner.TextContent, "admin@superstatus.io");
        StringAssert.Contains(banner.TextContent, "wiped hourly");

        // role=status so a screen reader announces the demo notice without stealing focus.
        Assert.AreEqual("status", banner.GetAttribute("role"));

        // The countdown renders a real MM:SS value on first paint, not the placeholder.
        var value = cut.Find(".demo-banner .reset .v").TextContent;
        Assert.IsTrue(
            System.Text.RegularExpressions.Regex.IsMatch(value, @"^([0-5]\d:[0-5]\d|now)$"),
            $"Countdown should render MM:SS (or 'now' mid-reset); got '{value}'.");
    }

    [TestMethod]
    public void WhenDemoDisabled_RendersNothingAtAll()
    {
        using var ctx = Ctx(demoEnabled: false);

        var cut = ctx.RenderComponent<DemoBanner>();

        Assert.AreEqual(string.Empty, cut.Markup.Trim(),
            "On a real deployment the banner component must emit no markup whatsoever.");
    }

    [TestMethod]
    public void WhenDemoDisabled_LeaksNoCredentialText()
    {
        // Belt and braces alongside the empty-markup assertion: if someone ever moves the
        // credentials outside the @if, this fails loudly rather than shipping them to prod.
        using var ctx = Ctx(demoEnabled: false);

        var cut = ctx.RenderComponent<DemoBanner>();

        Assert.IsFalse(cut.Markup.Contains("admin@superstatus.io", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(cut.Markup.Contains("demo", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DemoChrome_DoesNotRecolourSemanticStatusTokens()
    {
        // The design system reserves --status-degraded for "this service is degraded".
        // The demo chrome borrows the same amber, which is the one design risk flagged on
        // the issue. What must never happen is the reverse: demo mode reassigning the
        // token, so a healthy fleet renders as alarmed. The banner may *reference*
        // --status-degraded; it may not *redefine* it.
        var css = File.ReadAllText(FindRepoFile(Path.Combine(
            "SuperStatus.Web", "Components", "Layout", "DemoBanner.razor.css")));

        foreach (var token in new[] { "--status-up", "--status-degraded", "--status-down", "--status-unknown", "--accent" })
        {
            Assert.IsFalse(
                css.Contains($"{token}:", StringComparison.Ordinal),
                $"DemoBanner.razor.css redefines {token}. Demo chrome must consume the semantic "
                + "tokens, never reassign them — a status page's colours are load-bearing.");
        }
    }

    private static string FindRepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {name} from {AppContext.BaseDirectory}.");
    }
}
