namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. The static description of a check provider — its
    /// stable <see cref="TypeId"/>, a display name + icon for the selector, its
    /// versioned <see cref="ConfigSchema"/> (drives the generic edit form), and its
    /// declared <see cref="MetricDefs"/> (empty in Phase 1).
    /// </summary>
    public sealed class ProviderDescriptor
    {
        public ProviderDescriptor(
            string typeId,
            string displayName,
            string icon,
            ConfigSchema configSchema,
            IReadOnlyList<MetricDef>? metricDefs = null)
        {
            TypeId = typeId;
            DisplayName = displayName;
            Icon = icon;
            ConfigSchema = configSchema;
            MetricDefs = metricDefs ?? Array.Empty<MetricDef>();
        }

        /// <summary>Stable provider id stored in <c>StatusCheck.ProviderType</c> (e.g. <c>http</c>).</summary>
        public string TypeId { get; }

        /// <summary>Label shown in the provider selector (e.g. <c>HTTP(S)</c>).</summary>
        public string DisplayName { get; }

        /// <summary>Icon hint for the selector (a token the UI maps to a glyph).</summary>
        public string Icon { get; }

        /// <summary>The versioned config schema that drives the generic edit form.</summary>
        public ConfigSchema ConfigSchema { get; }

        /// <summary>Declared metrics. Empty in Phase 1.</summary>
        public IReadOnlyList<MetricDef> MetricDefs { get; }
    }
}
