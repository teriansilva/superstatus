using System.Text.Json;
using System.Text.Json.Nodes;
using SuperStatus.Data.Constants;

namespace SuperStatus.Services.Providers.Heartbeat
{
    /// <summary>
    /// Epic #271 / #320 Phase 2b. A <b>push / dead-man's-switch</b> provider for agents,
    /// cron jobs, and workers. Unlike http/ai it makes no outbound call — the agent pings
    /// SuperStatus each run (recorded as the check's last signal), and the engine passes
    /// that timestamp in via <see cref="ProbeContext.LastSignalUtc"/>. The probe is
    /// <b>up</b> while a ping is within <c>intervalSeconds + graceSeconds</c> and <b>down</b>
    /// once overdue. Binary alive/dead in v1; emits <c>seconds_since_heartbeat</c>.
    /// </summary>
    public sealed class HeartbeatCheckProvider : ICheckProvider
    {
        public const string TypeId = "heartbeat";
        public const int SchemaVersion = 1;

        public const string IntervalSecondsKey = "intervalSeconds";
        public const string GraceSecondsKey = "graceSeconds";
        public const string MetricSecondsSinceHeartbeat = "seconds_since_heartbeat";

        private static readonly ProviderDescriptor _descriptor = new(
            typeId: TypeId,
            displayName: "Agent heartbeat",
            icon: "pulse",
            configSchema: new ConfigSchema(SchemaVersion, new ConfigField[]
            {
                new(IntervalSecondsKey, "Expected interval (s)", ConfigFieldKind.Number, Required: true,
                    Help: "How often the agent is expected to ping.", Placeholder: "3600"),
                new(GraceSecondsKey, "Grace (s)", ConfigFieldKind.Number, Required: true,
                    Help: "Allowed lateness before the check goes down.", Placeholder: "300"),
            }),
            metricDefs: new MetricDef[]
            {
                new(MetricSecondsSinceHeartbeat, "Since heartbeat", "s", MetricKind.Gauge),
            });

        public ProviderDescriptor Descriptor => _descriptor;

        public Task<ProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken = default)
        {
            long interval = ReadLong(context.ConfigJson, IntervalSecondsKey);
            long grace = ReadLong(context.ConfigJson, GraceSecondsKey);
            long deadline = Math.Max(0, interval) + Math.Max(0, grace);

            // Age of the last ping. Never pinged (null) ⇒ infinitely old ⇒ overdue.
            double ageSeconds = context.LastSignalUtc is { } last
                ? Math.Max(0, (DateTime.UtcNow - last).TotalSeconds)
                : double.PositiveInfinity;

            bool overdue = !double.IsFinite(ageSeconds) || ageSeconds > deadline;

            // Emit the age only when finite (a "never pinged" age isn't a number).
            string? metricsJson = double.IsFinite(ageSeconds)
                ? new JsonObject { [MetricSecondsSinceHeartbeat] = Math.Round(ageSeconds, 1) }.ToJsonString()
                : null;

            var result = new ProbeResult
            {
                // Overdue ⇒ down (maps onto the existing Unreachable vocabulary so all
                // downstream — incidents/alerts/public status — is unchanged); else up.
                FailType = overdue ? FailType.Unreachable : FailType.NoFail,
                Reachable = !overdue,
                LatencyMs = 0,
                MetricsJson = metricsJson,
                Message = overdue
                    ? (double.IsFinite(ageSeconds) ? $"no heartbeat for {ageSeconds:0}s (expected within {deadline}s)" : "no heartbeat received yet")
                    : null,
            };
            return Task.FromResult(result);
        }

        private static long ReadLong(string? configJson, string key)
        {
            if (string.IsNullOrWhiteSpace(configJson)) return 0;
            try
            {
                using var doc = JsonDocument.Parse(configJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
                    if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
                }
            }
            catch (JsonException) { }
            return 0;
        }
    }
}
