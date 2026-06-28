namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. Wire shape of a check provider's descriptor — the API
    /// projects each registered provider to this and the Web edit dialog renders the
    /// Type selector + the generic config form from it. Lives in Data so both the
    /// ApiService (serialize) and the Web (deserialize) share one contract; the Web
    /// does not reference the Services project where the providers themselves live.
    /// </summary>
    public sealed class ProviderDescriptorViewModel
    {
        public string TypeId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public List<ProviderConfigFieldViewModel> Fields { get; set; } = new();

        /// <summary>#317: the metrics this provider emits (empty for http). The dashboard
        /// (Phase 2c) renders a check's MetricsJson against these typed definitions.</summary>
        public List<ProviderMetricDefViewModel> Metrics { get; set; } = new();
    }

    /// <summary>One declared metric a provider may emit (#317).</summary>
    public sealed class ProviderMetricDefViewModel
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;

        /// <summary>gauge | counter.</summary>
        public string Kind { get; set; } = "gauge";
        public double? WarnThreshold { get; set; }
        public double? CritThreshold { get; set; }
    }

    /// <summary>One config field in a provider's schema (the closed vocabulary).</summary>
    public sealed class ProviderConfigFieldViewModel
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;

        /// <summary>One of: text | number | bool | secret | select.</summary>
        public string Kind { get; set; } = "text";
        public bool Required { get; set; }
        public string? Help { get; set; }
        public string? Placeholder { get; set; }
        public List<ProviderConfigOptionViewModel> Options { get; set; } = new();
    }

    /// <summary>One option for a <c>select</c> field.</summary>
    public sealed class ProviderConfigOptionViewModel
    {
        public string Value { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }
}
