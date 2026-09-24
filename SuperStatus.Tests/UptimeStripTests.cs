using Bunit;
using SuperStatus.Web.Components.Hud;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #103: UptimeStrip renders one .day cell per element with the
/// correct status-derived class. Unknown values fall back to .gap so the
/// strip is never silently optimistic.
/// </summary>
[TestClass]
public class UptimeStripTests
{
    [TestMethod]
    public void EmitsOneCellPerDay()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<UptimeStrip>(p => p
            .Add(x => x.Days, new[] { "up", "up", "down" }));

        Assert.AreEqual(3, cut.FindAll(".uptime-strip .day").Count);
    }

    [TestMethod]
    [DataRow("up",        "day")]                 // baseline, no modifier class
    [DataRow("degraded",  "day degraded")]
    [DataRow("down",      "day down")]
    [DataRow("gap",       "day gap")]
    public void AppliesStatusClass(string value, string expectedClass)
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<UptimeStrip>(p => p
            .Add(x => x.Days, new[] { value }));

        var cell = cut.Find(".uptime-strip .day");
        Assert.AreEqual(expectedClass, cell.GetAttribute("class")?.Trim());
    }

    [TestMethod]
    public void UnknownValueFallsBackToGapNotUp()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<UptimeStrip>(p => p
            .Add(x => x.Days, new[] { "nonsense", "Unknown", "" }));

        // All three cells should carry .gap — none should be silently rendered up.
        foreach (var c in cut.FindAll(".uptime-strip .day"))
        {
            var cls = (c.GetAttribute("class") ?? "").Trim();
            Assert.AreEqual("day gap", cls);
        }
    }
}
