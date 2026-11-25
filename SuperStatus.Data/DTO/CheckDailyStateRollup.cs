namespace SuperStatus.Data.DTO
{
    /// <summary>
    /// Issue #138 (PR-B): one day's state tally for ONE check, carrying the
    /// check id so a single batched <c>GROUP BY (StatusCheckId, date)</c> can
    /// return the recent-window tallies for every check at once — the set-based
    /// counterpart of <see cref="DailyStateRollup"/>. Collapsing the dashboard's
    /// former per-check serial loop into a handful of these batched queries is
    /// what takes the cold summary sub-second regardless of check count.
    ///
    /// Worst-of-day for the uptime strip: <c>Down &gt; 0 ? down : Degraded &gt; 0 ? degraded : up</c>;
    /// Up = Total − Down − Degraded.
    /// </summary>
    public sealed record CheckDailyStateRollup(long StatusCheckId, DateTime Day, int Total, int Down, int Degraded, int Unreachable);
}
