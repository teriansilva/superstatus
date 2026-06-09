using System.ComponentModel.DataAnnotations.Schema;
using SuperStatus.Data.Constants;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #241/#253: the alert audit trail — one row per alert decision
    /// (fired or throttle-skipped) per channel, mirroring
    /// <see cref="WebhookExecutionLog"/>. In Phase A delivery is a no-op, so rows
    /// record "would have sent" / "throttled"; the email (Phase B) and web-push
    /// (Phase C) channels fill <see cref="Target"/> + <see cref="AlertOutcome.Failed"/>.
    /// </summary>
    public class AlertDeliveryLog : EntityBase
    {
        /// <summary>Owning status check. Cascades on delete.</summary>
        public long StatusCheckId { get; set; }

        [ForeignKey(nameof(StatusCheckId))]
        public virtual StatusCheck? StatusCheck { get; set; }

        /// <summary>Delivery channel the row is about.</summary>
        public AlertChannel Channel { get; set; }

        /// <summary>What made the alert fire (failure threshold / outage / recovery).</summary>
        public AlertTrigger Trigger { get; set; }

        public DateTime AttemptedUtc { get; set; }

        /// <summary>Recipient / push endpoint at the time of the attempt, denormalized.
        /// Null in Phase A (no channel wired).</summary>
        public string? Target { get; set; }

        public AlertOutcome Outcome { get; set; }

        /// <summary>Short human reason for the outcome (e.g. "throttled",
        /// "logged only (no delivery channel wired yet)").</summary>
        public string? Reason { get; set; }

        /// <summary>Truncated, sanitized error excerpt for failed deliveries (B/C).</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Maximum stored characters for <see cref="ErrorMessage"/>.</summary>
        public const int MaxErrorMessageLength = 1024;
    }
}
