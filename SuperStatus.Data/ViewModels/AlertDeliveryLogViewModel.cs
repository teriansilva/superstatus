using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Issue #241/#253: one alert decision for the admin audit table. Read-only
    /// projection of <see cref="AlertDeliveryLog"/> plus the owning check's title,
    /// mirroring <see cref="WebhookExecutionLogViewModel"/>.
    /// </summary>
    public class AlertDeliveryLogViewModel
    {
        public AlertDeliveryLogViewModel() { }

        public AlertDeliveryLogViewModel(AlertDeliveryLog log)
        {
            Id = log.Id;
            StatusCheckId = log.StatusCheckId;
            CheckTitle = log.StatusCheck?.Title ?? $"#{log.StatusCheckId}";
            AlertProfileId = log.AlertProfileId;
            AlertProfileName = log.AlertProfile?.Name;
            AttemptedUtc = log.AttemptedUtc;
            ChannelTypeId = log.ChannelTypeId;
            Trigger = log.Trigger;
            Target = log.Target;
            Outcome = log.Outcome;
            Reason = log.Reason;
            ErrorMessage = log.ErrorMessage;
        }

        public long Id { get; set; }
        public long StatusCheckId { get; set; }
        public string CheckTitle { get; set; } = string.Empty;

        /// <summary>#291 Phase C: the linked alert profile, resolved in the
        /// repository read (no schema change). Null on pre-#291 rows — the UI
        /// shows "—", mirroring the webhook log's WebhookName.</summary>
        public long? AlertProfileId { get; set; }
        public string? AlertProfileName { get; set; }

        public DateTime AttemptedUtc { get; set; }

        /// <summary>#343 Phase 5: the delivery channel's stable type id (was the
        /// <c>AlertChannel</c> enum). The admin panel renders it directly.</summary>
        public string ChannelTypeId { get; set; } = string.Empty;
        public AlertTrigger Trigger { get; set; }
        public string? Target { get; set; }
        public AlertOutcome Outcome { get; set; }
        public string? Reason { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
