namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #291 Phase A: a reusable alert delivery profile (which channels +
    /// which recipients). The alert RULES (thresholds / recovery / throttle minutes)
    /// stay per-check on <see cref="StatusCheck"/>; only delivery lives here.
    /// <para>
    /// #343 Phase 3: the enabled channels + their per-channel config now live in the
    /// <see cref="Channels"/> collection (<see cref="AlertProfileChannel"/>), the source
    /// of truth read by the engine + admin API. The <c>EmailEnabled</c> /
    /// <c>WebPushEnabled</c> / <c>EmailRecipients</c> / <c>UsesSiteDefaultRecipients</c>
    /// columns below are <b>deprecated</b> — retained + backfilled for one release so the
    /// migration is non-destructive; a later explicit migration drops them.
    /// </para>
    /// </summary>
    public class AlertProfile : EntityBase
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>#343 Phase 3: the enabled/configured delivery channels — the source
        /// of truth. Backfilled from the deprecated columns below.</summary>
        public virtual ICollection<AlertProfileChannel> Channels { get; set; } = new List<AlertProfileChannel>();

        /// <summary>Deprecated (#343 Phase 3) — superseded by an <c>email</c>
        /// <see cref="AlertProfileChannel"/>. Retained + backfilled for one release.</summary>
        public bool EmailEnabled { get; set; }

        /// <summary>Deprecated (#343 Phase 3) — moved into the <c>email</c> channel's
        /// <c>ConfigJson</c>. Comma/space-separated recipients.</summary>
        public string EmailRecipients { get; set; } = string.Empty;

        /// <summary>Deprecated (#343 Phase 3) — moved into the <c>email</c> channel's
        /// <c>ConfigJson</c>. Resolve recipients from SiteSettings.AlertDefaultRecipients
        /// at send time.</summary>
        public bool UsesSiteDefaultRecipients { get; set; }

        /// <summary>Deprecated (#343 Phase 3) — superseded by a <c>webpush</c>
        /// <see cref="AlertProfileChannel"/>.</summary>
        public bool WebPushEnabled { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
