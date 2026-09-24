using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// Phase 1 of #343. Everything a channel needs to deliver one alert, gathered by the
/// engine (<c>AlertEvaluator</c>) so the provider stays stateless — it owns only "how do
/// I deliver this", never scheduling, dedup, or state.
/// </summary>
public sealed class NotificationContext
{
    public NotificationContext(StatusCheck check, AlertTrigger trigger, string? recipientsOverride = null, string? configJson = null)
    {
        Check = check;
        Trigger = trigger;
        RecipientsOverride = recipientsOverride;
        ConfigJson = configJson;
    }

    /// <summary>The check the alert is about.</summary>
    public StatusCheck Check { get; }

    /// <summary>What made the alert fire (failure / outage / recovery).</summary>
    public AlertTrigger Trigger { get; }

    /// <summary>
    /// Resolved recipients for channels that address a target (email). Empty ⇒ the
    /// channel falls back to its site default (the <c>UsesSiteDefaultRecipients</c>
    /// profile). Channels that fan out to all subscribers (web push) ignore it.
    /// </summary>
    public string? RecipientsOverride { get; }

    /// <summary>#343 Phase 4: the delivering channel's stored <c>ConfigJson</c>, so a
    /// provider reads its own per-profile config (e.g. the webhook URL; Slack/Discord/
    /// Telegram creds in Phase 5). Null for channels with no config (web push).</summary>
    public string? ConfigJson { get; }
}
