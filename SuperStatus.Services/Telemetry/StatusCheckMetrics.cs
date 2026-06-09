using System.Diagnostics.Metrics;

namespace SuperStatus.Services.Telemetry
{
    /// <summary>
    /// Issue #86. Custom OpenTelemetry instruments for the status-check
    /// pipeline. Registered with OTel by name (<see cref="MeterName"/>) in
    /// <c>ServiceDefaults</c> and emitted from <c>StatusCheckService</c>.
    ///
    /// Cardinality discipline (Hermes review): instrument *identity* lives in
    /// tags, not in the instrument name or unit. Tags are deliberately
    /// low-cardinality — <c>fail_type</c> (4 values) and <c>outcome</c>
    /// (5 values). The per-check <c>title</c> is intentionally NOT a tag: on a
    /// large install it would explode the time-series count. Counters are
    /// unitless (a count); only the duration histogram carries a unit (ms).
    /// </summary>
    public static class StatusCheckMetrics
    {
        public const string MeterName = "SuperStatus.Services.StatusCheck";

        private static readonly Meter Meter = new(MeterName, "1.0.0");

        /// <summary>Count of status checks executed, tagged by <c>fail_type</c>.</summary>
        public static readonly Counter<long> ChecksExecuted =
            Meter.CreateCounter<long>("statuschecks.executed");

        /// <summary>Wall-clock execution duration in ms, tagged by <c>fail_type</c>.
        /// Recorded on failure/timeout too, so unreachable targets stay visible
        /// in the histogram instead of vanishing.</summary>
        public static readonly Histogram<double> CheckDuration =
            Meter.CreateHistogram<double>("statuschecks.duration", unit: "ms");

        /// <summary>Count of *actual* webhook fire attempts, tagged by
        /// <c>outcome</c>. Throttle-skipped webhooks are NOT counted here —
        /// they never hit the network (Hermes review).</summary>
        public static readonly Counter<long> WebhooksFired =
            Meter.CreateCounter<long>("statuschecks.webhooks_fired");

        /// <summary>Tag helper — keeps tag keys consistent across call sites.</summary>
        public static KeyValuePair<string, object?> FailTypeTag(object failType) => new("fail_type", failType.ToString());

        public static KeyValuePair<string, object?> OutcomeTag(object outcome) => new("outcome", outcome.ToString());
    }
}
