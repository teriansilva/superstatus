using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Services.Alerts;

/// <summary>What the evaluator should do with a tick's outcome.</summary>
public enum AlertAction
{
    /// <summary>No rule matched — nothing to log or send.</summary>
    None,
    /// <summary>Fire the alert to the enabled channels (and log it).</summary>
    Fire,
    /// <summary>A rule matched but the throttle suppressed it (log a Skipped row).</summary>
    Suppress,
}

/// <summary>
/// Issue #241/#253: the pure outcome of evaluating one tick against a check's
/// alert rules, plus the dedup/throttle bookkeeping to persist. Pure so the whole
/// transition table is unit-tested without a DB.
/// </summary>
public sealed record AlertDecision(
    AlertAction Action,
    AlertTrigger Trigger,
    DateTime? AlertedOutageDownSinceUtc,
    bool WriteAlertedOutage,
    DateTime? AlertLastFiredUtc,
    bool WriteAlertLastFired)
{
    public static readonly AlertDecision None =
        new(AlertAction.None, AlertTrigger.None, null, false, null, false);

    /// <summary>No alert, but clear a stale outage-episode marker (e.g. recovered
    /// from an outage that was only suppressed, never alerted). Keeps the cross-
    /// episode throttle anchor intact.</summary>
    public static AlertDecision ClearEpisode() =>
        new(AlertAction.None, AlertTrigger.None, AlertedOutageDownSinceUtc: null, WriteAlertedOutage: true, AlertLastFiredUtc: null, WriteAlertLastFired: false);

    public static AlertDecision Fire(AlertTrigger trigger, DateTime? episode, DateTime firedAt) =>
        new(AlertAction.Fire, trigger, episode, WriteAlertedOutage: true, firedAt, WriteAlertLastFired: true);

    /// <summary>A throttled outage is still <em>consumed</em> for dedup (the episode
    /// is marked handled) so it can't re-log every tick or fire late once the window
    /// expires — but it does NOT advance the last-fired anchor, so it never alerted.</summary>
    public static AlertDecision Suppress(AlertTrigger trigger, DateTime? episode) =>
        new(AlertAction.Suppress, trigger, episode, WriteAlertedOutage: true, null, WriteAlertLastFired: false);
}

/// <summary>
/// Issue #241/#253: the alert decision rules. Evaluated once per tick AFTER the
/// scheduler has updated <see cref="StatusCheck.ConsecutiveFailures"/> /
/// <see cref="StatusCheck.DownSinceUtc"/>. Dedups per outage episode (via
/// <see cref="StatusCheck.AlertedOutageDownSinceUtc"/>) so one outage yields one
/// alert and one recovery — never one per tick — and applies the per-check throttle.
/// </summary>
public static class AlertRules
{
    public static AlertDecision Decide(StatusCheck check, bool isFailure, DateTime nowUtc)
        // Legacy per-check anchors. #291 dispatch evaluates per linked profile
        // instead (anchors on the StatusCheckAlertProfile link row).
        => Decide(check, isFailure, nowUtc, check.AlertedOutageDownSinceUtc, check.AlertLastFiredUtc);

    /// <summary>
    /// #291: anchor-parameterised overload. The RULES (thresholds / recovery /
    /// throttle minutes) and the outage state (DownSinceUtc / ConsecutiveFailures)
    /// stay per-check; the dedup marker + throttle anchor are supplied by the
    /// caller — per (check, profile) link, so multiple linked profiles decide
    /// independently. Semantics are byte-identical to the per-check version.
    /// </summary>
    public static AlertDecision Decide(StatusCheck check, bool isFailure, DateTime nowUtc,
        DateTime? alertedOutageDownSinceUtc, DateTime? alertLastFiredUtc)
    {
        if (!isFailure)
        {
            // Recovery: fire once if we ACTUALLY ALERTED this outage (not merely
            // suppressed it), then clear the episode. A single terminal event,
            // exempt from throttle. "Actually alerted" = the last fire happened
            // within this episode (AlertLastFiredUtc >= the episode's down-since);
            // a throttled episode consumes the marker but never advances the anchor,
            // so it doesn't earn a recovery notice.
            bool firedThisOutage = check.AlertOnRecovery
                && alertedOutageDownSinceUtc is not null
                && alertLastFiredUtc is not null
                && alertLastFiredUtc >= alertedOutageDownSinceUtc;
            if (firedThisOutage)
                return AlertDecision.Fire(AlertTrigger.Recovery, episode: null, firedAt: nowUtc);

            // Healthy with a lingering marker we won't recover-announce (a suppressed
            // or recovery-disabled episode): clear it so it can't replay later.
            if (alertedOutageDownSinceUtc is not null)
                return AlertDecision.ClearEpisode();

            return AlertDecision.None;
        }

        // Failing. DownSinceUtc is stamped on the first failure; guard anyway.
        if (check.DownSinceUtc is null)
            return AlertDecision.None;

        // Already alerted this outage episode → no repeat (dedup key = the episode's DownSinceUtc).
        if (alertedOutageDownSinceUtc == check.DownSinceUtc)
            return AlertDecision.None;

        bool thresholdMet = check.AlertOnFailureThreshold > 0
            && check.ConsecutiveFailures >= check.AlertOnFailureThreshold;
        bool outageMet = check.AlertOnOutageMinutes > 0
            && (nowUtc - check.DownSinceUtc.Value).TotalMinutes >= check.AlertOnOutageMinutes;

        if (!thresholdMet && !outageMet)
            return AlertDecision.None;

        var trigger = outageMet ? AlertTrigger.Outage : AlertTrigger.Failure;

        bool throttled = alertLastFiredUtc is not null
            && (nowUtc - alertLastFiredUtc.Value).TotalMinutes < check.AlertThrottleMinutes;
        if (throttled)
            // Consume the episode (so it can't re-log every tick or fire once the
            // window expires); the last-fired anchor is left untouched.
            return AlertDecision.Suppress(trigger, episode: check.DownSinceUtc);

        return AlertDecision.Fire(trigger, episode: check.DownSinceUtc, firedAt: nowUtc);
    }
}
