namespace SuperStatus.Data.Constants
{
    // #343 Phase 5: the fixed `AlertChannel` enum audit column was replaced by a
    // channel-typeid string (`AlertDeliveryLog.ChannelTypeId`) so arbitrary channels
    // (Slack / Discord / Telegram / …) log correctly. The vocabulary now lives in
    // `SuperStatus.Data.Constants.NotificationChannelTypes`.

    /// <summary>Issue #241/#253: what made the alert fire.</summary>
    public enum AlertTrigger
    {
        /// <summary>Nothing fired (not persisted — internal "no action" sentinel).</summary>
        None = 0,
        /// <summary>Consecutive-failure threshold reached.</summary>
        Failure = 1,
        /// <summary>Sustained-outage duration reached.</summary>
        Outage = 2,
        /// <summary>Recovered (down → up).</summary>
        Recovery = 3,
    }

    /// <summary>Issue #241/#253: outcome of an alert decision, mirroring
    /// <see cref="WebhookOutcome"/>. In Phase A delivery is a no-op, so rows are
    /// <see cref="Fired"/> ("would send") or <see cref="Skipped"/> (throttled);
    /// <see cref="Failed"/> is used once channels actually send (B/C).</summary>
    public enum AlertOutcome
    {
        Fired = 0,
        Skipped = 1,
        Failed = 2,
    }
}
