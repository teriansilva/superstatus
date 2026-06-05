using System.ComponentModel.DataAnnotations.Schema;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #138 (P1): one persisted per-check, per-day state tally. Computed
    /// from raw <see cref="HistoricalStatusData"/> ticks by the rollup job, so
    /// the dashboard 30-day strip / uptime read ~30 tiny rows per check instead
    /// of aggregating millions of raw ticks on every request (the #136 cold
    /// path was still 21 s over 3.4 M rows). Unique on (StatusCheckId, Day).
    ///
    /// Worst-of-day: <c>Down &gt; 0 ? down : Degraded &gt; 0 ? degraded : up</c>;
    /// Up = Total − Down − Degraded.
    /// </summary>
    public class DailyStatusRollup : EntityBase
    {
        public long StatusCheckId { get; set; }

        [ForeignKey(nameof(StatusCheckId))]
        public virtual StatusCheck? StatusCheck { get; set; }

        /// <summary>UTC date (midnight) this tally covers.</summary>
        public DateTime Day { get; set; }

        public int Total { get; set; }
        public int Down { get; set; }
        public int Degraded { get; set; }

        /// <summary>
        /// #138 (PR-C1): of the <see cref="Down"/> samples, how many were
        /// unreachable (CheckFailed) vs a bad status code. Lets the detail-page /
        /// grid-modal overview keep its unreachable-vs-bad-status split for days
        /// served from the rollup (raw is pruned to ~72 h in PR-C2). Bad-status
        /// count ≈ <c>Down − Unreachable</c>; a tick that is both unreachable and
        /// status-mismatched counts once in Down and is attributed to Unreachable.
        /// </summary>
        public int Unreachable { get; set; }
    }
}
