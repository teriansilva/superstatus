namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #241 Phase C: a browser Web Push subscription registered by an operator
    /// from the console ("Enable notifications on this device"). One row per browser
    /// push endpoint; the alert engine fans an outage/recovery notification out to
    /// every row. Rows are pruned automatically when the push service reports the
    /// endpoint is gone (404/410) during a send.
    /// </summary>
    public class PushSubscription : EntityBase
    {
        /// <summary>The push service endpoint URL. Unique — identifies the subscription
        /// (a re-subscribe with the same endpoint updates the keys in place).</summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>Client P-256 ECDH public key (base64url) for payload encryption.</summary>
        public string P256dh { get; set; } = string.Empty;

        /// <summary>Client auth secret (base64url) for payload encryption.</summary>
        public string Auth { get; set; } = string.Empty;

        /// <summary>Optional user-agent captured at subscribe time, to label the device
        /// in the console. Not used for delivery.</summary>
        public string? UserAgent { get; set; }

        /// <summary>When the subscription was first registered.</summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>When a push was last delivered to this endpoint (informational).</summary>
        public DateTime? LastNotifiedUtc { get; set; }
    }
}
