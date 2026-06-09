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
            AttemptedUtc = log.AttemptedUtc;
            Channel = log.Channel;
            Trigger = log.Trigger;
            Target = log.Target;
            Outcome = log.Outcome;
            Reason = log.Reason;
            ErrorMessage = log.ErrorMessage;
        }

        public long Id { get; set; }
        public long StatusCheckId { get; set; }
        public string CheckTitle { get; set; } = string.Empty;
        public DateTime AttemptedUtc { get; set; }
        public AlertChannel Channel { get; set; }
        public AlertTrigger Trigger { get; set; }
        public string? Target { get; set; }
        public AlertOutcome Outcome { get; set; }
        public string? Reason { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
