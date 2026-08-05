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

        /// <summary>#343 Phase 5: the profile's schema-driven delivery channels (webhook /
        /// slack / discord / telegram — the ones with a per-channel <c>ConfigSchema</c>).
        /// Additive alongside the bespoke <see cref="EmailEnabled"/> / <see cref="WebPushEnabled"/>
        /// toggles, which stay as-is. Secret field values are never populated on read.</summary>
        public List<AlertProfileChannelViewModel> Channels { get; set; } = new();
    }

    /// <summary>
    /// #343 Phase 5: one configurable delivery channel on an alert profile — the
    /// schema-driven sibling of the bespoke Email/WebPush toggles. Carries the channel's
    /// stable type id, its enabled flag, and its config values keyed by
    /// <c>ConfigField.Key</c> (string-valued; bool as "true"/"false"). A <c>secret</c>
    /// field's stored value is never echoed on read; a blank secret on save preserves the
    /// stored one (the <c>ProviderConfigWriter</c> "leave blank to keep" rule).
    /// </summary>
    public class AlertProfileChannelViewModel
    {
        public string ProviderType { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public Dictionary<string, string> Config { get; set; } = new();

        /// <summary>#365: the <c>secret</c> field keys that already have a stored value on
        /// this channel — presence only, never the value (secrets are never echoed on read).
        /// The editor uses it to distinguish an <b>existing</b> secret ("leave blank to keep",
        /// not required) from a <b>brand-new</b> one (show the field's real placeholder + make
        /// it required, matching the server's rule) — closing the trap where a required secret
        /// field claimed it could be left blank and then 422'd on save.</summary>
        public List<string> StoredSecretKeys { get; set; } = new();
    }

    /// <summary>#365: request body for the per-channel test send
    /// (<c>POST /notifications/providers/{type}/test</c>). Carries the operator's typed config
    /// (a blank <c>secret</c> falls back to the channel's stored credential) and the owning
    /// profile id (0 ⇒ a not-yet-saved channel — the typed values are used as-is).</summary>
    public class ChannelTestRequest
    {
        public long ProfileId { get; set; }
        public Dictionary<string, string> Config { get; set; } = new();
    }

    /// <summary>#365: inline result of a per-channel test send — the unified channel outcome
    /// (<c>sent</c> / <c>skipped</c> / <c>failed</c>) plus a human detail. Nothing is persisted;
    /// a secret is never echoed (the detail is the provider's safe label, as in the audit log).</summary>
    public class ChannelTestResultViewModel
    {
        public string Outcome { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public string? Detail { get; set; }
    }

    /// <summary>#433: request body for a dry-run check probe
    /// (<c>POST /statuscheck/providers/{type}/test</c>). Carries the operator's typed config; a
    /// blank <c>secret</c> falls back to the stored one, but <b>only</b> when <see cref="CheckId"/>
    /// names a saved check whose own <c>ProviderType</c> matches the requested type — otherwise
    /// one provider could consume another's credential. 0 ⇒ an unsaved check; typed values
    /// stand alone.</summary>
    public class CheckTestRequest
    {
        public long CheckId { get; set; }
        public Dictionary<string, string> Config { get; set; } = new();
    }

    /// <summary>#433: one declared metric from a dry-run probe, already resolved against the
    /// provider's <c>MetricDefs</c> so the UI renders "TTFT 412 ms" rather than a raw key.</summary>
    public class CheckTestMetricViewModel
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public double Value { get; set; }
    }

    /// <summary>#433: inline result of a dry-run probe — the normalized verdict
    /// (<c>up</c> / <c>degraded</c> / <c>down</c>), latency, the provider's declared metrics, and
    /// a <b>scrubbed</b> message. Nothing is persisted and nothing reaches history, rollups or
    /// alerts. <see cref="Message"/> is never a provider's raw exception text or an upstream
    /// response body — see <c>ProbeResult.PublicMessage</c>.</summary>
    public class CheckTestResultViewModel
    {
        public string Outcome { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public long LatencyMs { get; set; }
        public int HttpStatusCode { get; set; }
        public string? Message { get; set; }
        public List<CheckTestMetricViewModel> Metrics { get; set; } = new();
    }

    /// <summary>#433: request body for AI model discovery
    /// (<c>POST /statuscheck/providers/ai/models</c>). A blank <see cref="ApiKey"/> falls back to
    /// the stored key, subject to the same matching-provider-type rule as
    /// <see cref="CheckTestRequest"/>.</summary>
    public class AiModelsRequest
    {
        public long CheckId { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
    }

    /// <summary>#433: what an OpenAI-compatible endpoint reports it can serve.
    /// <see cref="SupportsListing"/> false with <see cref="Ok"/> true is the normal, supported
    /// shape for an endpoint with no <c>/models</c> route — the dialog falls back to free text.
    /// <see cref="Message"/> is synthesised server-side; no upstream body is ever relayed.</summary>
    public class AiModelsResultViewModel
    {
        public bool Ok { get; set; }
        public bool SupportsListing { get; set; }
        public List<string> Models { get; set; } = new();
        public string? Message { get; set; }
    }
}
