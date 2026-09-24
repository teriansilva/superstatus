using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Issue #107 Phase 2: one webhook attempt for the admin audit table.
    /// Read-only projection of <see cref="WebhookExecutionLog"/> plus the
    /// owning check's title.
    /// </summary>
    public class WebhookExecutionLogViewModel
    {
        public WebhookExecutionLogViewModel() { }

        public WebhookExecutionLogViewModel(WebhookExecutionLog log)
        {
            Id = log.Id;
            StatusCheckId = log.StatusCheckId;
            CheckTitle = log.StatusCheck?.Title ?? $"#{log.StatusCheckId}";
            WebhookId = log.WebhookId;
            WebhookName = log.Webhook?.Name;
            AttemptedUtc = log.AttemptedUtc;
            TargetUrl = log.TargetUrl;
            HttpStatusCode = log.HttpStatusCode;
            ResponseTimeMs = log.ResponseTimeMs;
            Outcome = log.Outcome;
            ErrorMessage = log.ErrorMessage;
        }

        public long Id { get; set; }
        public long StatusCheckId { get; set; }
        public string CheckTitle { get; set; } = string.Empty;

        /// <summary>#291 Phase B: the linked webhook target this attempt was
        /// for. Null on pre-#291 rows (and after the target was deleted) —
        /// TargetUrl stays the audit-grade record either way.</summary>
        public long? WebhookId { get; set; }
        public string? WebhookName { get; set; }
        public DateTime AttemptedUtc { get; set; }
        public string TargetUrl { get; set; } = string.Empty;
        public int HttpStatusCode { get; set; }
        public int ResponseTimeMs { get; set; }
        public WebhookOutcome Outcome { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
