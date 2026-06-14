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
            ServiceLogoUrl = "https://" + string.Empty;
            Enabled = true;
            ExpectedStatusCode = 200;
            Description = string.Empty;
            IntervalSeconds = 60;   // #82: sensible default cadence for a new check
            AutoIncidentEnabled = false;   // #168: AI auto-incident opt-in, off by default
        }

        public StatusCheckViewModelBase(StatusCheck statusCheck)
        {
            Id = statusCheck.Id;
            Title = statusCheck.Title;
            StatusCheckUrl = statusCheck.StatusCheckUrl;
            ExpectedStatusCode = statusCheck.ExpectedStatusCode;
            // #293/#291 Phase D: the VM carries the EFFECTIVE slow threshold —
            // the linked SLA's — read-only, for display + the public detail
            // page's ComputeFailType. The legacy embedded column is gone.
            EffectiveSlowThresholdMs = statusCheck.Sla?.SlowThresholdMs ?? 0;
            LinkedSlaId = statusCheck.SlaId;
            LinkedSlaName = statusCheck.Sla?.Name;
            SlaTargetUptimePercent = statusCheck.Sla?.TargetUptimePercent ?? 100;
            SlaCriticalUptimePercent = statusCheck.Sla?.CriticalUptimePercent ?? 100;
            Description = statusCheck.Description;
            Enabled = statusCheck.Enabled;
            ServiceLogoUrl = statusCheck.ServiceLogoUrl;
            IntervalSeconds = statusCheck.IntervalSeconds;
            ConsecutiveFailures = statusCheck.ConsecutiveFailures;
            AutoIncidentEnabled = statusCheck.AutoIncidentEnabled;
            // #253: per-check alert rules (the internal dedup/throttle bookkeeping
            // AlertedOutageDownSinceUtc/AlertLastFiredUtc is server-managed, not surfaced).
            AlertOnFailureThreshold = statusCheck.AlertOnFailureThreshold;
            AlertOnOutageMinutes = statusCheck.AlertOnOutageMinutes;
            AlertOnRecovery = statusCheck.AlertOnRecovery;
            AlertThrottleMinutes = statusCheck.AlertThrottleMinutes;
        }

        public long Id { get; set; }
        public string Title { get; set; }
        public string StatusCheckUrl { get; set; }
        public int ExpectedStatusCode { get; set; }
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

        // ---- Issue #291 Phase D: legacy embedded fields — REJECTION-ONLY stubs ----
        // The /statuscheck/edit endpoint answers 422 when a payload carries any
        // of these with a non-empty value (the accepted-and-translated window of
        // Phases A–C is closed; see the release notes). They exist solely so an
        // old client's payload still BINDS and can be detected — they are never
        // populated on reads and never persisted.

        /// <summary>LEGACY (#291 Phase D): rejected with 422 when true.</summary>
        public bool IsWebHookOnErrorEnabled { get; set; }

        /// <summary>LEGACY (#291 Phase D): rejected with 422 when non-zero.</summary>
        public int ThrottleWebHookToExecuteOnlyEveryXMinutes { get; set; }

        /// <summary>LEGACY (#291 Phase D): rejected with 422 when non-empty.</summary>
        public string WebHookOnErrorUrl { get; set; } = string.Empty;

        /// <summary>LEGACY (#291 Phase D): rejected with 422 when true.</summary>
        public bool EmailAlertsEnabled { get; set; }

        /// <summary>LEGACY (#291 Phase D): rejected with 422 when non-empty.</summary>
        public string EmailRecipients { get; set; } = string.Empty;

        /// <summary>LEGACY (#291 Phase D): rejected with 422 when true.</summary>
        public bool WebPushAlertsEnabled { get; set; }

        // ---- Issue #291 Phase A/D: linked targets ----

        /// <summary>WRITE-side explicit link set: replace the check's webhook
        /// links with exactly these ids. Null = not provided → the check's
        /// existing links are left unchanged (since Phase D there is no legacy
        /// translation fallback). Distinct from <see cref="LinkedWebhookIds"/>
        /// so a fetched VM (which carries the read-only ids) round-trips
        /// without implying a write.</summary>
        public List<long>? WebhookIds { get; set; }

        /// <summary>WRITE-side explicit alert-profile link set; same contract
        /// as <see cref="WebhookIds"/>.</summary>
        public List<long>? AlertProfileIds { get; set; }

        /// <summary>READ-only round-trip: the check's linked webhook ids
        /// (server-managed, ignored on edit — like <see cref="ConsecutiveFailures"/>).</summary>
        public List<long> LinkedWebhookIds { get; set; } = new();

        /// <summary>READ-only round-trip: the check's linked alert-profile ids.</summary>
        public List<long> LinkedAlertProfileIds { get; set; } = new();

        // ---- Issue #293 Phase A/C: linked SLA ----

        /// <summary>WRITE-side explicit SLA link: link the check to exactly
        /// this SLA (422 if unknown). Null = not provided → an existing check
        /// keeps its current SLA; a new check links to the IsDefault SLA
        /// (since Phase C there is no legacy-ms translation fallback).</summary>
        public long? SlaId { get; set; }

        /// <summary>READ-only round-trip: the check's linked SLA id
        /// (server-managed, ignored on edit).</summary>
        public long? LinkedSlaId { get; set; }

        /// <summary>READ-only round-trip: the linked SLA's name, for display.</summary>
        public string? LinkedSlaName { get; set; }

        /// <summary>#293 Phase C: READ-only effective slow threshold — the
        /// linked SLA's SlowThresholdMs. Replaces the removed write-side
        /// ExpectedResponseTimeInMs for GET consumers (public detail page's
        /// ComputeFailType, the public API). Ignored on edit.</summary>
        public long EffectiveSlowThresholdMs { get; set; }

        /// <summary>#293 Phase B: the linked SLA's uptime targets, so the
        /// public detail page / live-status card can classify day cells via
        /// <c>SlaDayClassifier</c> without calling admin endpoints. READ-only
        /// (ignored on edit); defaults to the behavior-identical 100/100 when
        /// no SLA navigation was loaded, which is the historical
        /// worst-of-tick rule.</summary>
        public double SlaTargetUptimePercent { get; set; } = 100;

        /// <summary>#293 Phase B: see <see cref="SlaTargetUptimePercent"/>.</summary>
        public double SlaCriticalUptimePercent { get; set; } = 100;
    }
}
