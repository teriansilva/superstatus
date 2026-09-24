namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #291: a reusable outbound webhook target — the thing dispatch
    /// resolves, via the <see cref="StatusCheckWebhook"/> link table. The
    /// per-check embedded fields it replaced were dropped in Phase D
    /// (DropLegacyEmbeddedNotificationColumns).
    /// </summary>
    public class Webhook : EntityBase
    {
        /// <summary>Operator-facing label. Auto-named from the URL host (with a
        /// #N suffix on collision) when created by the legacy-field backfill.</summary>
        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        /// <summary>Disabled targets are skipped at dispatch with an audit row
        /// (reason "target disabled") — the link stays in place.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Minimum minutes between fires per linked check. The anchor
        /// lives on the link (<see cref="StatusCheckWebhook.LastFiredUtc"/>), so
        /// each (check, webhook) pair throttles independently.</summary>
        public int ThrottleMinutes { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
