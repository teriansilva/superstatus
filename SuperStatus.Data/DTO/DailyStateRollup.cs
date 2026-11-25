namespace SuperStatus.Data.DTO
{
    /// <summary>
    /// Issue #136: one day's state tally for a single status check, computed
    /// DB-side via <c>GROUP BY date</c> with conditional counts — NOT entities.
    /// Returning this small projection (≈30 rows/check) instead of materializing
    /// the raw 30-day tick window is what keeps the dashboard summary sub-second
    /// at multi-million-row scale.
    ///
    /// Worst-of-day for the uptime strip: <c>Down &gt; 0 ? down : Degraded &gt; 0 ? degraded : up</c>.
    /// </summary>
    public sealed record DailyStateRollup(DateTime Day, int Total, int Down, int Degraded, int Unreachable);
}
