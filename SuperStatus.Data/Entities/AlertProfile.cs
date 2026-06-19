namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #291 Phase A: a reusable alert delivery profile (which channels +
    /// which recipients). Replaces the per-check embedded delivery fields
    /// (EmailAlertsEnabled / EmailRecipients / WebPushAlertsEnabled — those
    /// columns stay until Phase D) via the <see cref="StatusCheckAlertProfile"/>
    /// link table. The alert RULES (thresholds / recovery / throttle minutes)
    /// stay per-check on <see cref="StatusCheck"/>; only delivery moves here.
    /// </summary>
    public class AlertProfile : EntityBase
    {
        public string Name { get; set; } = string.Empty;

        public bool EmailEnabled { get; set; }

        /// <summary>Comma/space-separated recipients. Empty is only valid when
        /// email is off or <see cref="UsesSiteDefaultRecipients"/> is set (the
        /// API rejects the remaining combination with 422).</summary>
        public string EmailRecipients { get; set; } = string.Empty;

        /// <summary>Resolve recipients from SiteSettings.AlertDefaultRecipients
        /// at send time — preserves the legacy "empty per-check recipients fall
        /// back to the site default" behaviour as an explicit, named profile.</summary>
        public bool UsesSiteDefaultRecipients { get; set; }

        public bool WebPushEnabled { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
