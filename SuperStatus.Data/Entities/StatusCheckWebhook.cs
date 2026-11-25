using System.ComponentModel.DataAnnotations.Schema;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #291 Phase A: n:m link between a check and a webhook target.
    /// Composite PK (StatusCheckId, WebhookId). Cascades with its check;
    /// RESTRICT on the webhook side (the API surfaces a 409 first, the DB
    /// constraint is the backstop).
    /// </summary>
    public class StatusCheckWebhook
    {
        public long StatusCheckId { get; set; }

        [ForeignKey(nameof(StatusCheckId))]
        public virtual StatusCheck? StatusCheck { get; set; }

        public long WebhookId { get; set; }

        [ForeignKey(nameof(WebhookId))]
        public virtual Webhook? Webhook { get; set; }

        /// <summary>
        /// Per-(check, webhook) throttle anchor. The legacy throttle anchored on
        /// the check's most recent successful webhook action
        /// (HistoricalStatusAction) — i.e. it only advanced on a Success — so
        /// this is stamped on Success only, preserving "a failed attempt is
        /// retried next tick" semantics. Null = never fired.
        /// </summary>
        public DateTime? LastFiredUtc { get; set; }
    }
}
