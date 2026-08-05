using System.ComponentModel.DataAnnotations.Schema;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Represents a status check that should be performed.
    /// </summary>
    public class StatusCheck : EntityBase
    {
        public string Title { get; set; }
        public string StatusCheckUrl { get; set; }
        public int ExpectedStatusCode { get; set; }
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public string ServiceLogoUrl { get; set; }

        // ---- Epic #271 / #312 Phase 1: pluggable check providers ----

        // Which check provider runs this check. Defaults to "http" (the only
        // provider in Phase 1); the migration backfills every existing row to it.
        // An unknown/missing value disables the check calmly (it never crashes the
        // scheduler or silently probes with defaults).
        public string ProviderType { get; set; } = "http";

        // Provider-specific configuration, validated against the provider's versioned
        // ConfigSchema. For "http" this mirrors StatusCheckUrl + ExpectedStatusCode
        // (which stay live and authoritative for old read consumers in Phase 1).
        // Nullable/empty on a pre-#312 row until the migration backfills it.
        public string? ConfigJson { get; set; }

        // ---- Epic #271 / #320 Phase 2b: agent-heartbeat (push) provider ----

        // The unguessable, URL-safe token an agent uses to ping us
        // (GET/POST /heartbeat/{token}). Indexed for O(1) ping lookup. Non-null only for
        // a "heartbeat" check; generated on create, rotatable.
        public string? HeartbeatToken { get; set; }

        // UTC of the last received heartbeat ping. Set to "now" on create (so a fresh
        // check has interval+grace before it can flip down) and updated by the ping
        // endpoint. The engine passes it to the provider via ProbeContext.LastSignalUtc;
        // the dead-man's-switch classifies down when it's older than interval+grace.
        public DateTime? LastHeartbeatUtc { get; set; }

        // Issue #82: per-check polling cadence in seconds. The scheduler skips
        // a check until lastTick + IntervalSeconds has elapsed. The migration
        // backfills existing rows to 10 (the old global JobIntervallInSeconds)
        // so cadence is unchanged on upgrade; new checks default to 60 and the
        // service clamps writes to 5–3600 (see StatusCheckSchedule).
        public int IntervalSeconds { get; set; }

        // Issue #83: count of consecutive failed ticks. Reset to 0 on the next
        // healthy (NoFail) result, incremented on each failure — in the same
        // save path as the result. The scheduler widens a failing check's
        // effective polling interval as this rises (exponential backoff,
        // capped) so we stop hammering a down endpoint. Migration backfills 0.
        public int ConsecutiveFailures { get; set; }

        // Issue #168: per-check opt-in for AI-authored incidents on sustained
        // downtime (default off). When on AND the instance's AI settings are
        // enabled, a check that stays down past AutoIncidentThresholdMinutes
        // gets an auto-drafted incident (Phase 2 trigger).
        public bool AutoIncidentEnabled { get; set; }

        // Issue #168: UTC of the healthy→failing transition (null while
        // healthy). Stamped on the first failure after a healthy result and
        // cleared on recovery — the precise "down since" the sustained-downtime
        // threshold measures against (robust to #83 widening the interval).
        public DateTime? DownSinceUtc { get; set; }

        // ---- Issue #241/#253: per-check alert rules (email + web-push, default off) ----

        // Alert once an outage reaches this many consecutive failures (0 = off).
        // Reuses ConsecutiveFailures (#83).
        public int AlertOnFailureThreshold { get; set; }

        // Alert once the check has been down >= this many minutes (0 = off),
        // measured from DownSinceUtc (same shape as the auto-incident threshold).
        public int AlertOnOutageMinutes { get; set; }

        // Also alert on the down->up recovery transition.
        public bool AlertOnRecovery { get; set; }

        // Minimum minutes between alerts for this check (storm guard).
        public int AlertThrottleMinutes { get; set; }

        // #291 Phase D: the legacy embedded notification fields
        // (IsWebHookOnErrorEnabled / WebHookOnErrorUrl / ThrottleWebHookToExecuteOnlyEveryXMinutes /
        // EmailAlertsEnabled / EmailRecipients / WebPushAlertsEnabled) and the
        // #293 legacy ExpectedResponseTimeInMs are GONE — dropped by the
        // DropLegacyEmbeddedNotificationColumns migration, which translates any
        // remaining legacy config into linked Webhook / AlertProfile / Sla rows
        // in raw SQL before dropping. Dispatch and classification resolve only
        // through the link tables / SlaId.

        // Dedup anchor: the DownSinceUtc of the outage episode already alerted, so
        // one outage produces one alert (not one per tick). Cleared on recovery.
        public DateTime? AlertedOutageDownSinceUtc { get; set; }

        // Throttle anchor: when the last alert for this check fired.
        public DateTime? AlertLastFiredUtc { get; set; }

        // ---- Issue #293 Phase A: linked SLA ----

        // Schema-nullable during the transition only: the startup backfill
        // assigns every check an SLA and fails startup if it can't, so the app
        // treats a missing link as an invariant violation (see
        // StatusCheckService.GetSlowThresholdMs). RESTRICT on delete — a linked
        // SLA can't be removed. The SLA is the only threshold source (#293
        // Phase C dropped the legacy ExpectedResponseTimeInMs column).
        public long? SlaId { get; set; }
        public Sla? Sla { get; set; }

        // First-seen timestamp, set once when the check is created. Survives the
        // 30-day historical cleanup window.
        public DateTime Created { get; set; }
    }
}