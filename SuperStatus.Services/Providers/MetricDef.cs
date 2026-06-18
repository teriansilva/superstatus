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
    /// to graph and alert on (no unbounded JSON blobs). <b>Phase 1 ships zero metrics</b>
    /// — every provider's <c>MetricDefs</c> is empty and <c>HistoricalStatusData.MetricsJson</c>
    /// stays null. The retention/query semantics are defined when Phase 2 lands.
    /// </summary>
    /// <param name="Key">Stable key used in <c>MetricsJson</c>.</param>
    /// <param name="Label">Human label for the dashboard.</param>
    /// <param name="Unit">Display unit (e.g. <c>ms</c>, <c>tok/s</c>).</param>
    /// <param name="Kind">Gauge or counter.</param>
    public sealed record MetricDef(string Key, string Label, string Unit, MetricKind Kind);
}
