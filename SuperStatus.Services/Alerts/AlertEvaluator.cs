using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Services.Alerts;

/// <summary>
/// Issue #241/#253: evaluates a check's alert rules once per tick and records the
/// decision to <see cref="AlertDeliveryLog"/>. #291 Phase A: delivery targets are
/// resolved ONLY through the StatusCheck↔AlertProfile link table — each linked
/// profile decides (fires / throttles / dedups) independently against its own
/// link anchors. The alert RULES and the outage state stay per-check. A fired
/// decision is dispatched to each of the profile's enabled channels — email via
/// <see cref="IEmailNotifier"/> (recipients from the profile, or the site default
/// for UsesSiteDefaultRecipients) and browser Web Push via
/// <see cref="IWebPushNotifier"/> — with the per-channel send result mapped to
/// the audit outcome. Mirrors the webhook gate→throttle→log shape
/// (<see cref="StatusCheckService"/>).
/// </summary>
public interface IAlertEvaluator
{
    Task EvaluateAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AlertEvaluator(
    IStatusCheckLinkRepository linkRepository,
    IAlertDeliveryLogRepository alertDeliveryLogRepository,
    IEmailNotifier emailNotifier,
    IWebPushNotifier webPushNotifier,
    ILogger<AlertEvaluator> logger) : IAlertEvaluator
{
    public async Task EvaluateAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default)
    {
        bool isFailure = failType != FailType.NoFail;

        // #291: no links → inert engine. Default-off stays default-off: a new
        // check has no links, so no rows and no behavior change.
        var links = await linkRepository.GetAlertProfileLinksAsync(check.Id, cancellationToken);
        if (links.Count == 0)
        {
            // Keep the legacy anchors honest while inert (Hermes, PR #294): a
            // healthy tick ends any legacy outage episode the same way the old
            // engine's recovery would have — otherwise a later first-time link
            // seeds the stale episode and replays its recovery.
            if (!isFailure && check.AlertedOutageDownSinceUtc is not null)
            {
                check.AlertedOutageDownSinceUtc = null;
                await linkRepository.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var now = DateTime.UtcNow;
        bool anchorsChanged = false;
        var pending = new List<(AlertProfile Profile, AlertDecision Decision, List<AlertChannel> Channels)>();

        foreach (var link in links)
        {
            var profile = link.AlertProfile!;
            var channels = EnabledChannels(profile);

            // Channels gate DELIVERY/logging, not state hygiene. A channel-less
            // profile is inert on failures (no rows), but a healthy tick still
            // clears its stale episode marker — otherwise enabling a channel
            // later could replay an old recovery. (Same rule as pre-#291.)
            if (channels.Count == 0)
            {
                if (!isFailure && link.AlertedOutageDownSinceUtc is not null)
                {
                    link.AlertedOutageDownSinceUtc = null;
                    anchorsChanged = true;
                }
                continue;
            }

            // Per-link anchors → each linked profile throttles/dedups independently.
            var decision = AlertRules.Decide(check, isFailure, now, link.AlertedOutageDownSinceUtc, link.AlertLastFiredUtc);

            if (decision.WriteAlertedOutage)
            {
                link.AlertedOutageDownSinceUtc = decision.AlertedOutageDownSinceUtc;
                anchorsChanged = true;
            }
            if (decision.WriteAlertLastFired)
            {
                link.AlertLastFiredUtc = decision.AlertLastFiredUtc;
                anchorsChanged = true;
            }

            if (decision.Action != AlertAction.None)
                pending.Add((profile, decision, channels));
        }

        // Persist the dedup/throttle bookkeeping BEFORE delivering (the link rows
        // are tracked) — same ordering as the pre-#291 per-check write.
        if (anchorsChanged)
            await linkRepository.SaveChangesAsync(cancellationToken);

        foreach (var (profile, decision, channels) in pending)
        {
            // Throttle-suppressed: one Skipped row per channel, no delivery.
            if (decision.Action == AlertAction.Suppress)
            {
                foreach (var channel in channels)
                    await SaveLogAsync(NoDeliveryLog(check.Id, profile.Id, channel, decision.Trigger, now, AlertOutcome.Skipped, "throttled"));
                continue;
            }

            // Fire: actually deliver per channel.
            foreach (var channel in channels)
            {
                if (channel == AlertChannel.Email)
                {
                    // #241 Phase B semantics unchanged: a guard skip (not configured /
                    // no recipients) is Skipped, an attempted-but-errored send is Failed.
                    // #291: recipients come from the profile; an empty override means
                    // "site default recipients" (the UsesSiteDefaultRecipients profile),
                    // matching the legacy empty-per-check-recipients fallback.
                    var recipients = profile.UsesSiteDefaultRecipients ? string.Empty : profile.EmailRecipients;
                    var result = await emailNotifier.SendAlertAsync(check, decision.Trigger, recipients, cancellationToken);
                    var (outcome, reason, error) = result.Status switch
                    {
                        EmailSendStatus.Sent => (AlertOutcome.Fired, "sent", (string?)null),
                        EmailSendStatus.Skipped => (AlertOutcome.Skipped, result.Detail, (string?)null),
                        _ => (AlertOutcome.Failed, (string?)null, result.Detail),
                    };
                    await SaveLogAsync(new AlertDeliveryLog
                    {
                        StatusCheckId = check.Id,
                        AlertProfileId = profile.Id,
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
                        AlertProfileId = profile.Id,
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
    }

    private static AlertDeliveryLog NoDeliveryLog(long checkId, long profileId, AlertChannel channel, AlertTrigger trigger, DateTime now, AlertOutcome outcome, string reason)
        => new()
        {
            StatusCheckId = checkId,
            AlertProfileId = profileId,
            Channel = channel,
            Trigger = trigger,
            AttemptedUtc = now,
            Target = null,
            Outcome = outcome,
            Reason = reason,
        };

    private static List<AlertChannel> EnabledChannels(AlertProfile profile)
    {
        var channels = new List<AlertChannel>(2);
        if (profile.EmailEnabled) channels.Add(AlertChannel.Email);
        if (profile.WebPushEnabled) channels.Add(AlertChannel.WebPush);
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
