namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Epic #271 / #317 Phase 2a. A check's recent metric series: the provider's declared
    /// metric definitions (so a consumer knows how to label/threshold) plus the per-tick
    /// values from the <b>recent raw ticks</b>. Retention is the raw-tick window only
    /// (≈72 h) — there is no metric rollup in 2a. Dashboard rendering is Phase 2c.
    /// </summary>
    public sealed class CheckMetricsViewModel
    {
        public long StatusCheckId { get; set; }
        public string ProviderType { get; set; } = string.Empty;

        /// <summary>The provider's declared metrics (empty for http).</summary>
        public List<ProviderMetricDefViewModel> MetricDefs { get; set; } = new();

        /// <summary>Per-tick samples, ascending by time. Only ticks that recorded metrics
        /// are included.</summary>
        public List<MetricSampleViewModel> Samples { get; set; } = new();
    }

    /// <summary>One tick's metric values.</summary>
    public sealed class MetricSampleViewModel
    {
        public DateTime TimeUtc { get; set; }

        /// <summary>Metric key → numeric value (only declared keys, validated on save).</summary>
        public Dictionary<string, double> Values { get; set; } = new();
    }
}
