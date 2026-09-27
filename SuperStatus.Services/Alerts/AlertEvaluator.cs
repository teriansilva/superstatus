using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Notifications;

namespace SuperStatus.Services.Alerts;

/// <summary>
/// Issue #241/#253: evaluates a check's alert rules once per tick and records the
/// decision to <see cref="AlertDeliveryLog"/>. #291 Phase A: delivery targets are
/// resolved ONLY through the StatusCheck↔AlertProfile link table — each linked
/// profile decides (fires / throttles / dedups) independently against its own
/// link anchors. The alert RULES and the outage state stay per-check. A fired
/// decision is dispatched to each of the profile's enabled channels through the
/// <see cref="INotificationProviderRegistry"/> (#343 Phase 1) — the channel's
/// provider delivers and its <see cref="NotificationSendResult"/> is mapped to the
/// audit outcome. #343 Phase 3: the enabled channels + their per-channel config come
/// from the profile's <see cref="AlertProfileChannel"/> collection (source of truth),
/// not the deprecated boolean columns; email resolves its recipients from the email
/// channel's config. Behavior is unchanged (the collection is backfilled 1:1 from the
/// columns). Mirrors the webhook gate→throttle→log shape
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
    INotificationProviderRegistry notificationRegistry,
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
        var pending = new List<(AlertProfile Profile, AlertDecision Decision, List<AlertProfileChannel> Channels)>();

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
                    await SaveLogAsync(NoDeliveryLog(check.Id, profile.Id, channel.ProviderType, decision.Trigger, now, AlertOutcome.Skipped, "throttled"));
                continue;
            }

            // Fire: deliver through each enabled channel's provider. The Sent/Skipped/
            // Failed → audit mapping is byte-identical to the pre-#343 per-channel code
            // (a guard skip stays Skipped; an attempted-but-errored send stays Failed).
            foreach (var channel in channels)
            {
                var result = await DeliverAsync(channel, check, decision.Trigger, cancellationToken);
                var (outcome, reason, error) = result.Outcome switch
                {
                    NotificationOutcome.Sent => (AlertOutcome.Fired, "sent", (string?)null),
                    NotificationOutcome.Skipped => (AlertOutcome.Skipped, result.Detail, (string?)null),
                    _ => (AlertOutcome.Failed, (string?)null, result.Detail),
                };
                await SaveLogAsync(new AlertDeliveryLog
                {
                    StatusCheckId = check.Id,
                    AlertProfileId = profile.Id,
                    ChannelTypeId = channel.ProviderType,
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

    /// <summary>
    /// Resolve the channel's provider and deliver, containing every failure mode so a
    /// bad channel can never disrupt the tick — the same contract the probe seam has.
    /// An unregistered channel is a calm <see cref="AlertOutcome.Skipped"/> (never a
    /// crash); a provider that throws is a <see cref="AlertOutcome.Failed"/> row.
    /// #291: email uses the profile's recipients (empty ⇒ site default); web push
    /// ignores them and fans out to every subscribed device — behavior unchanged.
    /// </summary>
    private async Task<NotificationSendResult> DeliverAsync(
        AlertProfileChannel channel, StatusCheck check, AlertTrigger trigger, CancellationToken cancellationToken)
    {
        var typeId = channel.ProviderType;
        var provider = notificationRegistry.Find(typeId);
        if (provider is null)
        {
            // Unknown/missing channel provider — surface calmly, never crash the tick.
            logger.LogWarning("No notification provider registered for channel '{TypeId}'.", typeId);
            return NotificationSendResult.Skipped("channel provider not registered");
        }

        // Email resolves recipients from its channel config (empty ⇒ site default),
        // preserving the pre-#343 behavior. Other channels (webhook, and the Phase-5
        // channels) read their own ConfigJson, which the context now carries.
        string? recipients = null;
        if (string.Equals(typeId, NotificationChannelTypes.Email, StringComparison.OrdinalIgnoreCase))
        {
            var settings = EmailChannelSettings.FromJson(channel.ConfigJson);
            recipients = settings.UsesSiteDefault ? string.Empty : settings.Recipients;
        }
        var context = new NotificationContext(check, trigger, recipients, channel.ConfigJson);

        try
        {
            return await provider.SendAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A misbehaving channel must never abort the tick. Record the type only
            // (avoid leaking any target/creds); the send becomes a Failed audit row.
            logger.LogWarning("Notification channel '{TypeId}' threw ({ExceptionType}).", typeId, ex.GetType().Name);
            return NotificationSendResult.Failed(string.Empty, "channel error");
        }
    }

    private static AlertDeliveryLog NoDeliveryLog(long checkId, long profileId, string channelTypeId, AlertTrigger trigger, DateTime now, AlertOutcome outcome, string reason)
        => new()
        {
            StatusCheckId = checkId,
            AlertProfileId = profileId,
            ChannelTypeId = channelTypeId,
            Trigger = trigger,
            AttemptedUtc = now,
            Target = null,
            Outcome = outcome,
            Reason = reason,
        };

    /// <summary>The profile's enabled delivery channels, ordered deterministically by
    /// type id. #343 Phase 3: the <see cref="AlertProfile.Channels"/> collection is the
    /// source of truth. A profile with no channel rows (e.g. one not yet reached by the
    /// backfill) falls back to the deprecated boolean columns — synthesized into transient
    /// channel rows — so delivery is byte-for-byte identical either way.</summary>
    private static List<AlertProfileChannel> EnabledChannels(AlertProfile profile)
    {
        IEnumerable<AlertProfileChannel> channels = profile.Channels.Count > 0
            ? profile.Channels
            : SynthesizeFromColumns(profile);
        return channels
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.ProviderType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Deprecated-column fallback: build transient channel rows from the legacy
    /// <c>EmailEnabled</c> / <c>WebPushEnabled</c> / <c>EmailRecipients</c> columns,
    /// matching exactly what the migration backfill + admin dual-write persist.</summary>
    private static IReadOnlyList<AlertProfileChannel> SynthesizeFromColumns(AlertProfile p) => new[]
    {
        new AlertProfileChannel
        {
            ProviderType = NotificationChannelTypes.Email,
            IsEnabled = p.EmailEnabled,
            ConfigJson = new EmailChannelSettings(p.EmailRecipients, p.UsesSiteDefaultRecipients).ToJson(),
        },
        new AlertProfileChannel
        {
            ProviderType = NotificationChannelTypes.WebPush,
            IsEnabled = p.WebPushEnabled,
            ConfigJson = null,
        },
    };

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
