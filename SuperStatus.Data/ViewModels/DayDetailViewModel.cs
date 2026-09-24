namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Issue #201: compact per-day breakdown for one cell of the 30-day uptime
    /// strip, fetched lazily on hover. Computed through the same canonical
    /// rollup-aware boundary as the strip itself (today on-the-fly, prior days
    /// from rollups). A day with no samples is <see cref="Status"/> = "gap",
    /// <see cref="Total"/> = 0.
    /// </summary>
    public class DayDetailViewModel
    {
        public long StatusCheckId { get; set; }
        public DateOnly Date { get; set; }

        /// <summary>Worst state of the day: up / degraded / down / gap (no samples).</summary>
        public string Status { get; set; } = "gap";

        public int Total { get; set; }
        public int Up { get; set; }
        public int Degraded { get; set; }
        public int Down { get; set; }
        public int Unreachable { get; set; }

        /// <summary>Sample-level uptime for the day (0–100); 0 when there are no samples.</summary>
        public double UptimePct { get; set; }

        /// <summary>#293 Phase B: the linked SLA's target uptime %, set ONLY
        /// when the SLA is not the behavior-identical 100/100 — the popover
        /// then suffixes the status word ("Up (SLA 95%)"). Null keeps today's
        /// exact wording for unmodified (default-SLA) instances.</summary>
        public double? SlaTargetPercent { get; set; }
    }
}
