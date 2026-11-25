namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #138 (PR-C1): a single-row, durable marker recording which version
    /// of the daily-rollup computation the persisted <see cref="DailyStatusRollup"/>
    /// rows were last (re)built with. The cleanup job does a one-time FULL
    /// re-backfill whenever the code's required version exceeds the stored one —
    /// covering fresh installs (no row → version 0) and upgrades that change the
    /// rollup schema/derivation (e.g. the added <see cref="DailyStatusRollup.Unreachable"/>
    /// column, which would otherwise stay 0 on already-rolled-up historical days).
    /// It also gates the raw-tick prune in PR-C2: raw is never pruned until the
    /// rollups are known complete for the retained window.
    /// </summary>
    public class RollupMaintenanceState : EntityBase
    {
        /// <summary>The rollup-computation version the persisted rollups reflect.</summary>
        public int BackfillVersion { get; set; }

        /// <summary>When the marker was last advanced.</summary>
        public DateTime UpdatedUtc { get; set; }
    }
}
