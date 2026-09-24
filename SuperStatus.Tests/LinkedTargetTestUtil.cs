using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #291: shared wiring for tests that exercise linked-target dispatch.
/// Phase D removed the legacy embedded fields and the backfill — dispatch
/// resolves through link rows only, so fixtures create the linked entities
/// explicitly (what the DropLegacyEmbeddedNotificationColumns migration's raw
/// SQL produces for an upgrader).
/// </summary>
internal static class LinkedTargetTestUtil
{
    public static LinkedTargetNormalizationService Normalization(SuperStatusDb db)
        => new(new StatusCheckLinkRepository(db),
            new Repository<Webhook>(db),
            new Repository<AlertProfile>(db));

    /// <summary>Create a webhook target and link it to the check — the shape
    /// the legacy-field translation used to produce.</summary>
    public static Webhook LinkWebhook(SuperStatusDb db, long statusCheckId, string url, int throttleMinutes = 0, DateTime? lastFiredUtc = null)
    {
        var webhook = new Webhook
        {
            Name = $"hook {db.WebhookSet.Count() + 1}",
            Url = url,
            IsEnabled = true,
            ThrottleMinutes = throttleMinutes,
            CreatedUtc = DateTime.UtcNow,
        };
        db.WebhookSet.Add(webhook);
        db.SaveChanges();
        db.StatusCheckWebhookSet.Add(new StatusCheckWebhook
        {
            StatusCheckId = statusCheckId,
            WebhookId = webhook.Id,
            LastFiredUtc = lastFiredUtc,
        });
        db.SaveChanges();
        return webhook;
    }

    /// <summary>Create an alert profile and link it to the check, seeding the
    /// link anchors from the check's episode/throttle columns (what the legacy
    /// translation carried over).</summary>
    public static AlertProfile LinkProfile(SuperStatusDb db, StatusCheck check,
        bool emailEnabled = true, string recipients = "", bool usesSiteDefaultRecipients = true, bool webPushEnabled = false)
    {
        var profile = new AlertProfile
        {
            Name = $"profile {db.AlertProfileSet.Count() + 1}",
            EmailEnabled = emailEnabled,
            EmailRecipients = recipients,
            UsesSiteDefaultRecipients = usesSiteDefaultRecipients,
            WebPushEnabled = webPushEnabled,
            CreatedUtc = DateTime.UtcNow,
        };
        db.AlertProfileSet.Add(profile);
        db.SaveChanges();
        // #343 Phase 3: the engine reads the channel collection (source of truth), so
        // mirror the flags into channel rows exactly as the migration backfill + admin
        // dual-write do (email carries recipients + site-default as its config).
        db.AlertProfileChannelSet.Add(new AlertProfileChannel
        {
            AlertProfileId = profile.Id,
            ProviderType = NotificationChannelTypes.Email,
            IsEnabled = emailEnabled,
            ConfigJson = new EmailChannelSettings(recipients, usesSiteDefaultRecipients).ToJson(),
        });
        db.AlertProfileChannelSet.Add(new AlertProfileChannel
        {
            AlertProfileId = profile.Id,
            ProviderType = NotificationChannelTypes.WebPush,
            IsEnabled = webPushEnabled,
            ConfigJson = null,
        });
        db.SaveChanges();
        db.StatusCheckAlertProfileSet.Add(new StatusCheckAlertProfile
        {
            StatusCheckId = check.Id,
            AlertProfileId = profile.Id,
            AlertedOutageDownSinceUtc = check.AlertedOutageDownSinceUtc,
            AlertLastFiredUtc = check.AlertLastFiredUtc,
        });
        db.SaveChanges();
        return profile;
    }
}
