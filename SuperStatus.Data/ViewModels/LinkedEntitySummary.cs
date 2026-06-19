using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Issue #291 Phase A: shared usage summary embedded in webhook AND
    /// alert-profile list/detail responses, and in the 409 delete-blocked
    /// payload — one shape for both entity types.
    /// </summary>
    public class LinkedEntitySummary
    {
        public long UsedByCount { get; set; }
        public List<string> LinkedCheckNames { get; set; } = new();

        public static LinkedEntitySummary From(List<string>? linkedCheckNames)
            => new()
            {
                UsedByCount = linkedCheckNames?.Count ?? 0,
                LinkedCheckNames = linkedCheckNames ?? new List<string>(),
            };
    }

    /// <summary>#291: webhook target + usage, for the /admin/webhooks surface.</summary>
    public class WebhookViewModel
    {
        public WebhookViewModel() { }

        public WebhookViewModel(Webhook webhook, List<string>? linkedCheckNames)
        {
            Id = webhook.Id;
            Name = webhook.Name;
            Url = webhook.Url;
            IsEnabled = webhook.IsEnabled;
            ThrottleMinutes = webhook.ThrottleMinutes;
            CreatedUtc = webhook.CreatedUtc;
            Usage = LinkedEntitySummary.From(linkedCheckNames);
        }

        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public int ThrottleMinutes { get; set; }
        public DateTime CreatedUtc { get; set; }
        public LinkedEntitySummary Usage { get; set; } = new();
    }

    /// <summary>
    /// #291 Phase B: outcome of a one-off operator test-fire
    /// (POST /admin/webhooks/{id}/test). Same wire fields as one execution-log
    /// row — but deliberately NOT persisted: WebhookExecutionLog.StatusCheckId
    /// is a required FK and a test has no triggering check, so the result is
    /// surfaced inline instead (no schema change in this phase).
    /// </summary>
    public class WebhookTestFireResult
    {
        public Constants.WebhookOutcome Outcome { get; set; }
        public int HttpStatusCode { get; set; }
        public int ResponseTimeMs { get; set; }
        public string? ErrorMessage { get; set; }
        public string TargetUrl { get; set; } = string.Empty;
    }

    /// <summary>#291: alert profile + usage, for the /admin/alert-profiles surface.</summary>
    public class AlertProfileViewModel
    {
        public AlertProfileViewModel() { }

        public AlertProfileViewModel(AlertProfile profile, List<string>? linkedCheckNames)
        {
            Id = profile.Id;
            Name = profile.Name;
            EmailEnabled = profile.EmailEnabled;
            EmailRecipients = profile.EmailRecipients;
            UsesSiteDefaultRecipients = profile.UsesSiteDefaultRecipients;
            WebPushEnabled = profile.WebPushEnabled;
            CreatedUtc = profile.CreatedUtc;
            Usage = LinkedEntitySummary.From(linkedCheckNames);
        }

        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool EmailEnabled { get; set; }
        public string EmailRecipients { get; set; } = string.Empty;
        public bool UsesSiteDefaultRecipients { get; set; }
        public bool WebPushEnabled { get; set; }
        public DateTime CreatedUtc { get; set; }
        public LinkedEntitySummary Usage { get; set; } = new();
    }
}
