using Bunit;
using SuperStatus.Web.Components.Layout;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #177 — the topbar theme toggle. The mode cycling itself is JS
/// (hud-theme.js) + exercised by the visual harness; here we assert the control
/// renders as an accessible icon button defaulting to the "system" label.
/// </summary>
[TestClass]
public class ThemeToggleTests
{
    [TestMethod]
    public void RendersAccessibleIconButton_DefaultingToSystem()
    {
        using var ctx = new BunitTestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // hudTheme.get returns default

        var cut = ctx.RenderComponent<ThemeToggle>();

        var btn = cut.Find("button.hud-icon-btn");
        // With no JS value the control shows the system default + an aria-label.
        StringAssert.StartsWith(btn.GetAttribute("aria-label"), "Theme:");
        StringAssert.Contains(btn.GetAttribute("aria-label"), "system");
        Assert.IsNotNull(btn.QuerySelector("svg"), "renders a mode icon");
    }
}
