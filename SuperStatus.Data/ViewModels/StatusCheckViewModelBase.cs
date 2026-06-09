using Microsoft.Extensions.Primitives;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    public class StatusCheckViewModelBase
    {
        public StatusCheckViewModelBase() 
        {
            Title = string.Empty;  
            StatusCheckUrl = string.Empty;
            WebHookOnErrorUrl = string.Empty;
            ServiceLogoUrl = "https://" + string.Empty;
            Enabled = true;
            ExpectedStatusCode = 200;
            ExpectedResponseTimeInMs = 1000;
            Description = string.Empty;
            IsWebHookOnErrorEnabled = false;
            IntervalSeconds = 60;   // #82: sensible default cadence for a new check
            AutoIncidentEnabled = false;   // #168: AI auto-incident opt-in, off by default
            EmailRecipients = string.Empty;   // #253: alert rule defaults — all off
        }

        public StatusCheckViewModelBase(StatusCheck statusCheck)
        {
            Id = statusCheck.Id;
            Title = statusCheck.Title;
            StatusCheckUrl = statusCheck.StatusCheckUrl;
            IsWebHookOnErrorEnabled = statusCheck.IsWebHookOnErrorEnabled;
            WebHookOnErrorUrl = statusCheck.WebHookOnErrorUrl;
            ExpectedStatusCode = statusCheck.ExpectedStatusCode;
            ExpectedResponseTimeInMs = statusCheck.ExpectedResponseTimeInMs;
            Description = statusCheck.Description;
            Enabled = statusCheck.Enabled;
            ServiceLogoUrl = statusCheck.ServiceLogoUrl;
            IntervalSeconds = statusCheck.IntervalSeconds;
            ThrottleWebHookToExecuteOnlyEveryXMinutes = statusCheck.ThrottleWebHookToExecuteOnlyEveryXMinutes;
            ConsecutiveFailures = statusCheck.ConsecutiveFailures;
            AutoIncidentEnabled = statusCheck.AutoIncidentEnabled;
            // #253: per-check alert rules (the internal dedup/throttle bookkeeping
            // AlertedOutageDownSinceUtc/AlertLastFiredUtc is server-managed, not surfaced).
            AlertOnFailureThreshold = statusCheck.AlertOnFailureThreshold;
            AlertOnOutageMinutes = statusCheck.AlertOnOutageMinutes;
            AlertOnRecovery = statusCheck.AlertOnRecovery;
            AlertThrottleMinutes = statusCheck.AlertThrottleMinutes;
            EmailAlertsEnabled = statusCheck.EmailAlertsEnabled;
            EmailRecipients = statusCheck.EmailRecipients;
            WebPushAlertsEnabled = statusCheck.WebPushAlertsEnabled;
        }

        public long Id { get; set; }
        public string Title { get; set; }
        public string StatusCheckUrl { get; set; }
        public bool IsWebHookOnErrorEnabled { get; set; }
        public int ThrottleWebHookToExecuteOnlyEveryXMinutes { get; set; }
        public string WebHookOnErrorUrl { get; set; }
        public int ExpectedStatusCode { get; set; }
        public long ExpectedResponseTimeInMs { get; set; }
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public string ServiceLogoUrl { get; set; }

        /// <summary>Issue #82: per-check polling cadence (seconds). Clamped to
        /// 5–3600 server-side on save.</summary>
        public int IntervalSeconds { get; set; }

        /// <summary>Issue #83: consecutive failed ticks (read-only telemetry).
        /// Drives the scheduler's exponential backoff; managed server-side.</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>Issue #168: opt this check into AI-authored incidents on
        /// sustained downtime (default off). Only fires when the instance's AI
        /// settings are enabled.</summary>
        public bool AutoIncidentEnabled { get; set; }

        // ---- Issue #241/#253: per-check alert rules (default off) ----

        /// <summary>Alert once an outage reaches this many consecutive failures (0 = off).</summary>
        public int AlertOnFailureThreshold { get; set; }

        /// <summary>Alert once the check has been down ≥ this many minutes (0 = off).</summary>
        public int AlertOnOutageMinutes { get; set; }

        /// <summary>Also alert on the down→up recovery transition.</summary>
        public bool AlertOnRecovery { get; set; }

        /// <summary>Minimum minutes between alerts for this check (storm guard).</summary>
        public int AlertThrottleMinutes { get; set; }

        /// <summary>Email alerts enabled (recipients in <see cref="EmailRecipients"/>).</summary>
        public bool EmailAlertsEnabled { get; set; }

        /// <summary>Comma/space-separated email recipients for this check.</summary>
        public string EmailRecipients { get; set; } = string.Empty;

        /// <summary>Web-push alerts enabled (delivery in Phase C).</summary>
        public bool WebPushAlertsEnabled { get; set; }
    }
}