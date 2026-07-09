using System.ComponentModel.DataAnnotations.Schema;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #291 Phase A: n:m link between a check and an alert profile.
    /// Composite PK (StatusCheckId, AlertProfileId). Cascades with its check;
    /// RESTRICT on the profile side (API 409 first, DB backstop).
    /// </summary>
    public class StatusCheckAlertProfile
    {
        public long StatusCheckId { get; set; }

        [ForeignKey(nameof(StatusCheckId))]
        public virtual StatusCheck? StatusCheck { get; set; }

        public long AlertProfileId { get; set; }

        [ForeignKey(nameof(AlertProfileId))]
        public virtual AlertProfile? AlertProfile { get; set; }

        // #291: the alert DELIVERY anchors move per-(check, profile) so multiple
        // linked profiles fire/throttle independently. Outage tracking itself
        // (StatusCheck.DownSinceUtc / ConsecutiveFailures) stays per-check —
        // there is one outage; what's per-link is whether THIS profile already
        // handled it and when it last fired. Mirrors the per-check pair
        // AlertedOutageDownSinceUtc / AlertLastFiredUtc (kept until Phase D).

        /// <summary>Dedup marker: the DownSinceUtc of the outage episode this
        /// profile already handled (fired or throttle-consumed). Cleared on recovery.</summary>
        public DateTime? AlertedOutageDownSinceUtc { get; set; }

        /// <summary>Throttle anchor: when this profile last actually fired for
        /// this check (any channel — the legacy throttle was cross-channel too).</summary>
        public DateTime? AlertLastFiredUtc { get; set; }
    }
}
