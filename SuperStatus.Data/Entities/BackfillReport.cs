namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #291 Phase A: persisted summary of an automatic legacy→linked-target
    /// backfill run. One row per startup run that actually changed something
    /// (idempotent re-runs that find nothing to do write no row) — the audit
    /// trail for unattended `compose pull` upgrades.
    /// </summary>
    public class BackfillReport : EntityBase
    {
        /// <summary>What was backfilled (e.g. "linked-targets").</summary>
        public string Kind { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; }

        /// <summary>JSON summary of created entities/links per check.</summary>
        public string SummaryJson { get; set; } = string.Empty;
    }
}
