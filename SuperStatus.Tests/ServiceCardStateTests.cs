using SuperStatus.Data.Constants;
using SuperStatus.Web.Components.StatusCheckOverview;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #95 Phase 3c — the FailType → frame-accent / LED / label mapping
/// behind StatusCheckOverviewCard's HudPanel frame.
/// </summary>
[TestClass]
public class ServiceCardStateTests
{
    [TestMethod]
    [DataRow(FailType.NoFail,       "",         "up",       "OPERATIONAL")]
    [DataRow(FailType.ResponseTime, "",         "degraded", "DEGRADED")]
    [DataRow(FailType.StatusCode,   "critical", "down",     "DOWN")]
    [DataRow(FailType.Unreachable,  "critical", "down",     "DOWN")]
    public void MapsFailType(FailType f, string accent, string led, string label)
    {
        Assert.AreEqual(accent, ServiceCardState.FrameAccent(f));
        Assert.AreEqual(led, ServiceCardState.LedStatus(f));
        Assert.AreEqual(label, ServiceCardState.StateLabel(f));
    }

    [TestMethod]
    public void NullFailType_IsUnknown_NotCritical()
    {
        Assert.AreEqual("", ServiceCardState.FrameAccent(null));
        Assert.AreEqual("unknown", ServiceCardState.LedStatus(null));
        Assert.AreEqual("UNKNOWN", ServiceCardState.StateLabel(null));
    }
}
