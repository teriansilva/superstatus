using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Services.Alerts;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #241/#253: the pure alert decision rules — the full fire/skip transition
/// table (threshold / outage / recovery / throttle / episode-dedup).
/// </summary>
[TestClass]
public class AlertRulesTests
{
    private static readonly DateTime Now = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);

    private static StatusCheck Check(
        int threshold = 0, int outageMin = 0, bool onRecovery = false, int throttleMin = 0,
        int consecutive = 0, DateTime? downSince = null, DateTime? alertedEpisode = null, DateTime? lastFired = null)
        => new()
        {
            AlertOnFailureThreshold = threshold,
            AlertOnOutageMinutes = outageMin,
            AlertOnRecovery = onRecovery,
            AlertThrottleMinutes = throttleMin,
            ConsecutiveFailures = consecutive,
            DownSinceUtc = downSince,
            AlertedOutageDownSinceUtc = alertedEpisode,
            AlertLastFiredUtc = lastFired,
        };

    [TestMethod]
    public void Threshold_crossed_fires_once_andStampsEpisode()
    {
        var down = Now.AddMinutes(-1);
        var d = AlertRules.Decide(Check(threshold: 3, consecutive: 3, downSince: down), isFailure: true, Now);
        Assert.AreEqual(AlertAction.Fire, d.Action);
        Assert.AreEqual(AlertTrigger.Failure, d.Trigger);
        Assert.IsTrue(d.WriteAlertedOutage);
        Assert.AreEqual(down, d.AlertedOutageDownSinceUtc);
        Assert.AreEqual(Now, d.AlertLastFiredUtc);
    }

    [TestMethod]
    public void BelowThreshold_noFire()
    {
        var d = AlertRules.Decide(Check(threshold: 3, consecutive: 2, downSince: Now.AddMinutes(-1)), isFailure: true, Now);
        Assert.AreEqual(AlertAction.None, d.Action);
    }

    [TestMethod]
    public void AlreadyAlertedThisEpisode_noFire()
    {
        var down = Now.AddMinutes(-5);
        var d = AlertRules.Decide(Check(threshold: 1, consecutive: 9, downSince: down, alertedEpisode: down), isFailure: true, Now);
        Assert.AreEqual(AlertAction.None, d.Action, "one outage → one alert, not one per tick");
    }

    [TestMethod]
    public void Outage_durationReached_fires_asOutageTrigger()
    {
        var d = AlertRules.Decide(Check(outageMin: 5, downSince: Now.AddMinutes(-6)), isFailure: true, Now);
        Assert.AreEqual(AlertAction.Fire, d.Action);
        Assert.AreEqual(AlertTrigger.Outage, d.Trigger);
    }

    [TestMethod]
    public void Outage_notYetReached_noFire()
    {
        var d = AlertRules.Decide(Check(outageMin: 5, downSince: Now.AddMinutes(-2)), isFailure: true, Now);
        Assert.AreEqual(AlertAction.None, d.Action);
    }

    [TestMethod]
    public void BothConfigured_firesOnce_outageTakesPrecedence()
    {
        var d = AlertRules.Decide(Check(threshold: 2, outageMin: 5, consecutive: 9, downSince: Now.AddMinutes(-6)), isFailure: true, Now);
        Assert.AreEqual(AlertAction.Fire, d.Action);
        Assert.AreEqual(AlertTrigger.Outage, d.Trigger);
    }

    [TestMethod]
    public void Throttled_suppresses_butConsumesEpisode_withoutAdvancingAnchor()
    {
        var down = Now.AddMinutes(-3);
        var d = AlertRules.Decide(
            Check(threshold: 1, consecutive: 5, downSince: down, lastFired: Now.AddMinutes(-1), throttleMin: 10),
            isFailure: true, Now);
        Assert.AreEqual(AlertAction.Suppress, d.Action);
        // Episode is consumed so it can't re-log every tick or fire once the window expires...
        Assert.IsTrue(d.WriteAlertedOutage);
        Assert.AreEqual(down, d.AlertedOutageDownSinceUtc);
        // ...but the last-fired anchor is NOT advanced — a throttled episode never alerted.
        Assert.IsFalse(d.WriteAlertLastFired);
    }

    [TestMethod]
    public void Recovery_afterAlertedOutage_fires_andClearsEpisode()
    {
        // An alerted outage left both the episode marker and the fire anchor set
        // (anchor >= the episode's down-since).
        var d = AlertRules.Decide(
            Check(onRecovery: true, alertedEpisode: Now.AddMinutes(-10), lastFired: Now.AddMinutes(-10)),
            isFailure: false, Now);
        Assert.AreEqual(AlertAction.Fire, d.Action);
        Assert.AreEqual(AlertTrigger.Recovery, d.Trigger);
        Assert.IsTrue(d.WriteAlertedOutage);
        Assert.IsNull(d.AlertedOutageDownSinceUtc, "episode cleared on recovery");
    }

    [TestMethod]
    public void Recovery_afterThrottledOutage_doesNotFire_butClearsTheStaleMarker()
    {
        // The episode was consumed by a throttle-suppress (marker at -5), but the
        // last fire belongs to an OLDER episode (-30) → this outage never alerted,
        // so there's no recovery to announce — but the stale marker is cleared so it
        // can't replay later.
        var d = AlertRules.Decide(
            Check(onRecovery: true, alertedEpisode: Now.AddMinutes(-5), lastFired: Now.AddMinutes(-30)),
            isFailure: false, Now);
        Assert.AreEqual(AlertAction.None, d.Action);
        Assert.IsTrue(d.WriteAlertedOutage);
        Assert.IsNull(d.AlertedOutageDownSinceUtc, "stale marker cleared on healthy");
    }

    [TestMethod]
    public void Healthy_withNoMarker_isFullyInert()
    {
        var d = AlertRules.Decide(Check(onRecovery: true), isFailure: false, Now);
        Assert.AreEqual(AlertAction.None, d.Action);
        Assert.IsFalse(d.WriteAlertedOutage, "nothing to clear → no write");
    }

    [TestMethod]
    public void Recovery_withoutPriorOutageAlert_noFire()
    {
        var d = AlertRules.Decide(Check(onRecovery: true, alertedEpisode: null), isFailure: false, Now);
        Assert.AreEqual(AlertAction.None, d.Action);
    }

    [TestMethod]
    public void Recovery_disabled_noFire()
    {
        var d = AlertRules.Decide(Check(onRecovery: false, alertedEpisode: Now.AddMinutes(-10)), isFailure: false, Now);
        Assert.AreEqual(AlertAction.None, d.Action);
    }

    [TestMethod]
    public void SteadyHealthy_noFire()
        => Assert.AreEqual(AlertAction.None, AlertRules.Decide(Check(), isFailure: false, Now).Action);
}
