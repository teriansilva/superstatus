using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Services.Alerts;

/// <summary>
/// Issue #241/#253: evaluates a check's alert rules once per tick and records the
/// decision to <see cref="AlertDeliveryLog"/>. A fired decision is dispatched to each
/// enabled channel — email via <see cref="IEmailNotifier"/> (Phase B) and browser
/// Web Push via <see cref="IWebPushNotifier"/> (Phase C) — with the per-channel send
/// result mapped to the audit outcome. Mirrors the webhook gate→throttle→log shape
/// (<see cref="StatusCheckService"/>).
/// </summary>
public interface IAlertEvaluator
{
    Task EvaluateAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AlertEvaluator(
    IStatusCheckRepository statusCheckRepository,
    IAlertDeliveryLogRepository alertDeliveryLogRepository,
    IEmailNotifier emailNotifier,
    IWebPushNotifier webPushNotifier,
    ILogger<AlertEvaluator> logger) : IAlertEvaluator
{
    public async Task EvaluateAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default)
    {
        bool isFailure = failType != FailType.NoFail;
        var channels = EnabledChannels(check);

        // Channels gate DELIVERY/logging, not state hygiene. With no channel enabled
        // the engine is inert on failures (no rows, no behavior change), but a healthy
        // tick still clears any stale episode marker left from when a channel WAS
        // enabled — otherwise re-enabling a channel later could replay an old recovery.
        if (channels.Count == 0)
        {
            if (!isFailure && check.AlertedOutageDownSinceUtc is not null)
            {
                check.AlertedOutageDownSinceUtc = null;
                await statusCheckRepository.UpdateAndSave(check, cancellationToken);
            }
            return;
        }

        var now = DateTime.UtcNow;
        var decision = AlertRules.Decide(check, isFailure, now);

        // Persist the dedup/throttle bookkeeping on the check (tracked in this scope).
        // Applied even for a None decision so a stale episode marker gets cleared on
        // recovery without emitting a log.
        bool changed = false;
        if (decision.WriteAlertedOutage)
        {
            check.AlertedOutageDownSinceUtc = decision.AlertedOutageDownSinceUtc;
            changed = true;
        }
        if (decision.WriteAlertLastFired)
        {
            check.AlertLastFiredUtc = decision.AlertLastFiredUtc;
            changed = true;
        }
        if (changed)
            await statusCheckRepository.UpdateAndSave(check, cancellationToken);

        if (decision.Action == AlertAction.None)
            return; // routine "rule not met / already alerted / marker cleared" — no log.

        // Throttle-suppressed: one Skipped row per channel, no delivery.
        if (decision.Action == AlertAction.Suppress)
        {
            foreach (var channel in channels)
                await SaveLogAsync(NoDeliveryLog(check.Id, channel, decision.Trigger, now, AlertOutcome.Skipped, "throttled"));
            return;
        }

        // Fire: actually deliver per channel.
        foreach (var channel in channels)
        {
            if (channel == AlertChannel.Email)
            {
                // #241 Phase B: send the email. A guard skip (not configured / no
                // recipients) is Skipped, an attempted-but-errored send is Failed —
                // distinct, so an unconfigured channel isn't shown as a delivery outage.
                var result = await emailNotifier.SendAlertAsync(check, decision.Trigger, cancellationToken);
                var (outcome, reason, error) = result.Status switch
                {
                    EmailSendStatus.Sent => (AlertOutcome.Fired, "sent", (string?)null),
                    EmailSendStatus.Skipped => (AlertOutcome.Skipped, result.Detail, (string?)null),
                    _ => (AlertOutcome.Failed, (string?)null, result.Detail),
                };
                await SaveLogAsync(new AlertDeliveryLog
                {
                    StatusCheckId = check.Id,
                    Channel = channel,
                    Trigger = decision.Trigger,
                    AttemptedUtc = now,
                    Target = string.IsNullOrEmpty(result.Target) ? null : result.Target,
                    Outcome = outcome,
                    Reason = reason,
                    ErrorMessage = error,
                });
            }
            else
            {
                // #241 Phase C: fan the push out to every subscribed device. A guard skip
                // (no VAPID keys / no devices / all expired) is Skipped; an attempted-but-
                // errored fan-out is Failed — same Skipped-vs-Failed distinction as email.
                var result = await webPushNotifier.SendAlertAsync(check, decision.Trigger, cancellationToken);
                var (outcome, reason, error) = result.Status switch
                {
                    WebPushSendStatus.Sent => (AlertOutcome.Fired, "sent", (string?)null),
                    WebPushSendStatus.Skipped => (AlertOutcome.Skipped, result.Detail, (string?)null),
                    _ => (AlertOutcome.Failed, (string?)null, result.Detail),
                };
                await SaveLogAsync(new AlertDeliveryLog
                {
                    StatusCheckId = check.Id,
                    Channel = channel,
                    Trigger = decision.Trigger,
                    AttemptedUtc = now,
                    Target = string.IsNullOrEmpty(result.Target) ? null : result.Target,
                    Outcome = outcome,
                    Reason = reason,
                    ErrorMessage = error,
                });
            }
        }
    }

    private static AlertDeliveryLog NoDeliveryLog(long checkId, AlertChannel channel, AlertTrigger trigger, DateTime now, AlertOutcome outcome, string reason)
        => new()
        {
            StatusCheckId = checkId,
            Channel = channel,
            Trigger = trigger,
            AttemptedUtc = now,
            Target = null,
            Outcome = outcome,
            Reason = reason,
        };

    private static List<AlertChannel> EnabledChannels(StatusCheck check)
    {
        var channels = new List<AlertChannel>(2);
        if (check.EmailAlertsEnabled) channels.Add(AlertChannel.Email);
        if (check.WebPushAlertsEnabled) channels.Add(AlertChannel.WebPush);
        return channels;
    }

    private async Task SaveLogAsync(AlertDeliveryLog log)
    {
        try
        {
            await alertDeliveryLogRepository.AddAndSave(log);
        }
        catch (Exception ex)
        {
            // Best-effort audit, like the webhook log — never abort the tick because
            // the audit table is unhappy.
            logger.LogError(ex, "Failed to persist AlertDeliveryLog for check {CheckId}", log.StatusCheckId);
        }
    }
}
