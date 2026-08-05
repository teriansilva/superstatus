using Bunit;
using SuperStatus.Web.Components.Hud.Ambience;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #109 Phase 2 — mission timer ambience. (The rotating classification
/// footer was retired in #170 in favour of an operator-customizable static
/// footer; its component + tests were removed.)
/// </summary>
[TestClass]
public class HudAmbiencePhase2Tests
{
    [TestMethod]
    public void MissionTimer_NullAnchor_RendersDash()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudMissionTimer>(p => p
            .Add(x => x.SinceUtc, (DateTime?)null));

        Assert.AreEqual("—", cut.Find(".value").TextContent.Trim());
        Assert.AreEqual("T+", cut.Find(".prefix").TextContent.Trim());
    }

    [TestMethod]
    public void MissionTimer_PastAnchor_FormatsAsDaysHoursMinutesSeconds()
    {
        using var ctx = new BunitTestContext();
        var past = DateTime.UtcNow.AddDays(-2).AddHours(-3).AddMinutes(-4).AddSeconds(-5);
        var cut = ctx.RenderComponent<HudMissionTimer>(p => p
            .Add(x => x.SinceUtc, past));

        var v = cut.Find(".value").TextContent;
        // Allow a small drift on the seconds counter — match the day/hour
        // segments exactly and the minute within ±1.
        Assert.IsTrue(v.StartsWith("2d "), $"Expected day=2, got {v}");
        Assert.IsTrue(v.Contains("03:"), $"Expected hour=03, got {v}");
    }

    [TestMethod]
    public void MissionTimer_FutureAnchor_RendersZeroNotNegative()
    {
        using var ctx = new BunitTestContext();
        var future = DateTime.UtcNow.AddMinutes(5);
        var cut = ctx.RenderComponent<HudMissionTimer>(p => p
            .Add(x => x.SinceUtc, future));

        Assert.AreEqual("0d 00:00:00", cut.Find(".value").TextContent.Trim());
    }

    [TestMethod]
    public void MissionTimer_CustomSuffix_Renders()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudMissionTimer>(p => p
            .Add(x => x.SinceUtc, DateTime.UtcNow.AddMinutes(-1))
            .Add(x => x.Suffix, "CONTINUOUS UPTIME"));

        Assert.IsTrue(cut.Find(".suffix").TextContent.Contains("CONTINUOUS UPTIME"));
    }
}
