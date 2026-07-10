namespace SuperStatus.Data.Constants
{
    /// <summary>
    /// #343 Phase 3: stable notification-channel type ids stored on
    /// <see cref="Entities.AlertProfileChannel.ProviderType"/> and matched against the
    /// notification-provider registry's <c>TypeId</c>. Defined in Data (the lowest
    /// layer) so the entity, the migration backfill, the admin API, and the Services
    /// providers all share one vocabulary.
    /// </summary>
    public static class NotificationChannelTypes
    {
        public const string Email = "email";
        public const string WebPush = "webpush";

        /// <summary>#343 Phase 4: outgoing webhook (POSTs a JSON alert payload).</summary>
        public const string Webhook = "webhook";

        /// <summary>#343 Phase 5: Slack incoming webhook.</summary>
        public const string Slack = "slack";

        /// <summary>#343 Phase 5: Discord webhook.</summary>
        public const string Discord = "discord";

        /// <summary>#343 Phase 5: Telegram bot API (bot token + chat id).</summary>
        public const string Telegram = "telegram";
    }
}
