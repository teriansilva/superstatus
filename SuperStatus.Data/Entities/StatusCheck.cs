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
        public bool IsWebHookOnErrorEnabled { get; set; }
        public string WebHookOnErrorUrl { get; set; }
        public int ThrottleWebHookToExecuteOnlyEveryXMinutes { get; set; }
        public int ExpectedStatusCode { get; set; }
        public long ExpectedResponseTimeInMs { get; set; }
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public string ServiceLogoUrl { get; set; }

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

        // First-seen timestamp, set once when the check is created. Survives the
        // 30-day historical cleanup window.
        public DateTime Created { get; set; }
    }
}