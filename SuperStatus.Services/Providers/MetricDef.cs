using System.Text.Json;

namespace SuperStatus.Services.Providers
{
    /// <summary>Gauge vs. counter — how a declared metric accumulates.</summary>
    public enum MetricKind
    {
        Gauge = 0,
        Counter = 1,
    }

    /// <summary>
    /// Epic #271. A <b>typed-up-front</b> metric a provider may emit. A provider may
    /// only emit metrics it declared here, so history / dashboard / API have a schema
    /// to graph and alert on (no unbounded JSON blobs). Phase 1 shipped zero metrics;
    /// Phase 2a (#317) populates them: a provider emits a flat <c>{ key: number }</c>
    /// object into <c>HistoricalStatusData.MetricsJson</c>, validated against its
    /// declared defs before persisting.
    /// </summary>
    /// <param name="Key">Stable key used in <c>MetricsJson</c>.</param>
    /// <param name="Label">Human label for the dashboard.</param>
    /// <param name="Unit">Display unit (e.g. <c>ms</c>, <c>tok/s</c>).</param>
    /// <param name="Kind">Gauge or counter.</param>
    /// <param name="WarnThreshold">Optional value at/above (gauge) which the metric reads "degraded" on a dashboard.</param>
    /// <param name="CritThreshold">Optional value at/above which it reads "critical".</param>
    public sealed record MetricDef(
        string Key,
        string Label,
        string Unit,
        MetricKind Kind,
        double? WarnThreshold = null,
        double? CritThreshold = null);

    /// <summary>
    /// Epic #271 / #317 Phase 2a. Validates a provider's emitted metrics against its
    /// declared <see cref="MetricDef"/>s and returns the <b>sanitized</b> JSON to persist:
    /// only declared keys with numeric values survive (no unbounded blobs). Returns null
    /// when there is nothing valid to store (so the column stays null, as in Phase 1).
    /// </summary>
    public static class MetricsValidator
    {
        public static string? Sanitize(string? metricsJson, IReadOnlyList<MetricDef> declared)
        {
            if (string.IsNullOrWhiteSpace(metricsJson) || declared.Count == 0) return null;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(metricsJson);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
            if (root.ValueKind != JsonValueKind.Object) return null;

            var allowed = declared.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
            var kept = new System.Text.Json.Nodes.JsonObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (!allowed.Contains(prop.Name)) continue;                 // undeclared key — drop
                if (prop.Value.ValueKind != JsonValueKind.Number) continue; // non-numeric — drop
                if (prop.Value.TryGetDouble(out var v) && double.IsFinite(v))
                {
                    kept[prop.Name] = v;
                }
            }
            return kept.Count == 0 ? null : kept.ToJsonString();
        }
    }
}
