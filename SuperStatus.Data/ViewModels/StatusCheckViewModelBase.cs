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
    }
}