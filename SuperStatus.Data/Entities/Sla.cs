namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #293: a reusable, named SLA target — the thing the checker reads
    /// its slow threshold from, via <see cref="StatusCheck.SlaId"/>, and the
    /// day classifier reads its Target/Critical tolerances from. The per-check
    /// embedded ExpectedResponseTimeInMs it replaced was dropped in Phase C
    /// (DropLegacyEmbeddedNotificationColumns).
    /// </summary>
    public class Sla : EntityBase
    {
        /// <summary>Operator-facing label. Auto-named "Default" /
        /// "Legacy &lt;N&gt; ms" (with a #N suffix on collision) when created
        /// by the legacy-field backfill.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Target uptime in percent (0–100).</summary>
        public double TargetUptimePercent { get; set; }

        /// <summary>Critical uptime in percent (0–100); must be ≤ Target.</summary>
        public double CriticalUptimePercent { get; set; }

        /// <summary>A tick slower than this many milliseconds (but otherwise
        /// healthy) is marked degraded at collection time (≥ 1).</summary>
        public long SlowThresholdMs { get; set; }

        /// <summary>Exactly one SLA is the default (enforced by a partial
        /// unique index); new checks without an explicit SLA link to it.</summary>
        public bool IsDefault { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
