namespace SuperStatus.Services.Scheduling
{
    /// <summary>
    /// Issue #82. Pure per-check scheduling rules — the single source of truth
    /// for "is this check due?" and the allowed cadence bounds. Kept free of EF
    /// / DI so it is trivially unit-testable; the scheduler derives a check's
    /// last tick from the most recent persisted <c>HistoricalStatusData</c>
    /// (durable across restarts / multi-instance) and asks <see cref="IsDue"/>.
    /// </summary>
    public static class StatusCheckSchedule
    {
        /// <summary>Floor — status checks run at most every 30 s. More frequent
        /// is too expensive (a DB write + outbound HTTP per check per tick) for
        /// the value it adds on a small self-hosted footprint. Enforced on every
        /// check by <see cref="EffectiveIntervalSeconds"/>, including legacy rows
        /// stored below it (#136).</summary>
        public const int MinIntervalSeconds = 30;

        /// <summary>Ceiling — anything rarer than an hour should be a real cron.</summary>
        public const int MaxIntervalSeconds = 3600;

        /// <summary>Default cadence for a newly created check.</summary>
        public const int DefaultIntervalSeconds = 60;

        /// <summary>Backfill for rows that predate the column — the old global
        /// <c>JobIntervallInSeconds</c>, so cadence is unchanged on upgrade.</summary>
        public const int LegacyIntervalSeconds = 10;

        /// <summary>Clamp an operator-supplied interval into the allowed range.</summary>
        public static int Clamp(int intervalSeconds) =>
            Math.Clamp(intervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);

        /// <summary>
        /// True when a check should run now. A check that has never run
        /// (<paramref name="lastCheckUtc"/> is null) is always due (first run).
        /// The interval is floored at 1 s so a corrupt/zero stored value can
        /// never make a check fire every tick forever.
        /// </summary>
        public static bool IsDue(DateTime? lastCheckUtc, int intervalSeconds, DateTime nowUtc)
        {
            if (lastCheckUtc is null) return true;
            int effective = Math.Max(1, intervalSeconds);
            return lastCheckUtc.Value.AddSeconds(effective) <= nowUtc;
        }

        /// <summary>
        /// Issue #83: the effective polling interval for a check given its base
        /// cadence and current consecutive-failure count. The FIRST failure
        /// still polls at the base interval (a normal retry); each failure
        /// beyond the first doubles the interval, capped at
        /// <see cref="MaxIntervalSeconds"/>. A recovered check
        /// (<paramref name="consecutiveFailures"/> back to 0) returns to base
        /// immediately. Pure — the scheduler feeds the result into
        /// <see cref="IsDue"/>.
        /// </summary>
        public static int EffectiveIntervalSeconds(int baseIntervalSeconds, int consecutiveFailures)
        {
            // #136: floor at MinIntervalSeconds (not 1) so the scheduler enforces
            // the 30 s minimum even on legacy/manual rows stored below it.
            int baseSeconds = Math.Max(MinIntervalSeconds, baseIntervalSeconds);
            if (consecutiveFailures <= 1) return baseSeconds;            // healthy or first failure → base
            int exponent = Math.Min(consecutiveFailures - 1, 30);       // cap exponent (overflow guard)
            long scaled = (long)baseSeconds << exponent;                // base × 2^(failures-1)
            return (int)Math.Min(scaled, MaxIntervalSeconds);
        }
    }
}
