using System.ComponentModel.DataAnnotations.Schema;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// #343 Phase 3: one enabled/configured delivery channel on an
    /// <see cref="AlertProfile"/> — the generalization of the profile's hardcoded
    /// <c>EmailEnabled</c> / <c>WebPushEnabled</c> / <c>EmailRecipients</c> columns into a
    /// per-profile, per-channel collection. A profile has at most one row per
    /// <see cref="ProviderType"/> (unique index). This is what lets a channel the engine
    /// didn't hardcode (Slack / Discord / Telegram, #343 Phase 5) carry its own
    /// per-profile config with no schema change.
    /// <para>
    /// Behavior-preserving transition: the legacy <see cref="AlertProfile"/> boolean/text
    /// columns are retained (deprecated, backfilled) for one release; the engine + admin
    /// API read this collection as the source of truth.
    /// </para>
    /// </summary>
    public class AlertProfileChannel : EntityBase
    {
        public long AlertProfileId { get; set; }

        [ForeignKey(nameof(AlertProfileId))]
        public virtual AlertProfile? AlertProfile { get; set; }

        /// <summary>Stable channel type id (e.g. <c>email</c>, <c>webpush</c>) — matches
        /// the notification-provider registry's <c>TypeId</c>. See
        /// <see cref="Constants.NotificationChannelTypes"/>.</summary>
        public string ProviderType { get; set; } = string.Empty;

        /// <summary>Whether this channel delivers for the profile. A disabled row is kept
        /// (so its config survives a toggle) but skipped at send time.</summary>
        public bool IsEnabled { get; set; }

        /// <summary>Channel-specific config as JSON (validated against the channel's
        /// schema when one exists). For <c>email</c> this carries the recipients +
        /// site-default flag; <c>webpush</c> needs none (null). Secret fields follow the
        /// write-only/masked rule when a credential-bearing channel lands (#343).</summary>
        public string? ConfigJson { get; set; }
    }
}
