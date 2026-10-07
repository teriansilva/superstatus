using SuperStatus.Services.Scheduling;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #82 — pure per-check scheduling rules (clamp + due calculation).
/// </summary>
[TestClass]
public class StatusCheckScheduleTests
{
    private static readonly DateTime Now = new(2026, 5, 29, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void IsDue_NeverRun_IsAlwaysDue()
        => Assert.IsTrue(StatusCheckSchedule.IsDue(null, 3600, Now), "First-ever run must be due.");

    [TestMethod]
    public void IsDue_IntervalNotElapsed_IsNotDue()
        => Assert.IsFalse(StatusCheckSchedule.IsDue(Now.AddSeconds(-30), 60, Now));

    [TestMethod]
    public void IsDue_IntervalExactlyElapsed_IsDue()
        => Assert.IsTrue(StatusCheckSchedule.IsDue(Now.AddSeconds(-60), 60, Now));

    [TestMethod]
    public void IsDue_IntervalLongElapsed_IsDue()
        => Assert.IsTrue(StatusCheckSchedule.IsDue(Now.AddHours(-2), 60, Now));

    [TestMethod]
    public void IsDue_ZeroInterval_FlooredToOneSecond_NotEveryTick()
    {
        // A corrupt 0 must not make the check fire continuously: floored to 1 s.
        Assert.IsFalse(StatusCheckSchedule.IsDue(Now, 0, Now), "Just ran → not due even with a 0 interval.");
        Assert.IsTrue(StatusCheckSchedule.IsDue(Now.AddSeconds(-1), 0, Now));
    }

    [TestMethod]
    public void Clamp_BelowMin_ClampsToMin()
        => Assert.AreEqual(StatusCheckSchedule.MinIntervalSeconds, StatusCheckSchedule.Clamp(1));

    [TestMethod]
    public void Clamp_AboveMax_ClampsToMax()
        => Assert.AreEqual(StatusCheckSchedule.MaxIntervalSeconds, StatusCheckSchedule.Clamp(99_999));

    [TestMethod]
    public void Clamp_WithinRange_Unchanged()
        => Assert.AreEqual(60, StatusCheckSchedule.Clamp(60));

    [TestMethod]
    public void Bounds_AreStable()
    {
        // #136: floor raised 5 → 30 s.
        Assert.AreEqual(30, StatusCheckSchedule.MinIntervalSeconds);
        Assert.AreEqual(3600, StatusCheckSchedule.MaxIntervalSeconds);
        Assert.AreEqual(60, StatusCheckSchedule.DefaultIntervalSeconds);
        Assert.AreEqual(10, StatusCheckSchedule.LegacyIntervalSeconds);
    }

    [TestMethod]
    public void EffectiveInterval_FloorsLegacyBelow30_To30()
    {
        // #136: the scheduler enforces the 30 s floor even on legacy rows
        // stored at 10 s (the #82 backfill) and manual sub-30 values.
        Assert.AreEqual(30, StatusCheckSchedule.EffectiveIntervalSeconds(10, 0), "legacy 10 s → 30 s");
        Assert.AreEqual(30, StatusCheckSchedule.EffectiveIntervalSeconds(5, 0));
        Assert.AreEqual(60, StatusCheckSchedule.EffectiveIntervalSeconds(60, 0), "≥30 unchanged");
        // Backoff still works above the floor: base 60, 2 fails → 120.
        Assert.AreEqual(120, StatusCheckSchedule.EffectiveIntervalSeconds(60, 2));
    }

    [TestMethod]
    public void IsDue_LegacyStored10s_TreatedAs30s_ViaEffectiveInterval()
    {
        // A check that ran 20 s ago, stored interval 10 s: under the floor it's
        // NOT due yet (effective 30 s), but at 35 s it is.
        var ran = Now.AddSeconds(-20);
        int eff = StatusCheckSchedule.EffectiveIntervalSeconds(10, 0);
        Assert.IsFalse(StatusCheckSchedule.IsDue(ran, eff, Now), "20 s elapsed < 30 s floor → not due");
        Assert.IsTrue(StatusCheckSchedule.IsDue(Now.AddSeconds(-35), eff, Now), "35 s elapsed ≥ 30 s → due");
        Assert.IsTrue(StatusCheckSchedule.IsDue(null, eff, Now), "never-run still due");
    }
}
