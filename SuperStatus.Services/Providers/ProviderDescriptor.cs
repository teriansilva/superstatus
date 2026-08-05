using SuperStatus.Services.Plugins;

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
        /// <summary>Default per-probe ceiling when a provider doesn't specify one.</summary>
        public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(10);

        public ProviderDescriptor(
            string typeId,
            string displayName,
            string icon,
            ConfigSchema configSchema,
            IReadOnlyList<MetricDef>? metricDefs = null,
            TimeSpan? probeTimeout = null,
            string? description = null,
            ProbeDirection direction = ProbeDirection.Pull,
            string? batchTargetField = null)
        {
            TypeId = typeId;
            DisplayName = displayName;
            Icon = icon;
            ConfigSchema = configSchema;
            MetricDefs = metricDefs ?? Array.Empty<MetricDef>();
            ProbeTimeout = probeTimeout ?? DefaultProbeTimeout;
            Description = description ?? string.Empty;
            Direction = direction;
            BatchTargetField = batchTargetField;
        }

        /// <summary>Stable provider id stored in <c>StatusCheck.ProviderType</c> (e.g. <c>http</c>).</summary>
        public string TypeId { get; }

        /// <summary>Label shown in the provider selector (e.g. <c>HTTP(S)</c>).</summary>
        public string DisplayName { get; }

        /// <summary>Icon hint for the selector (a token the UI maps to a glyph).</summary>
        public string Icon { get; }

        /// <summary>The versioned config schema that drives the generic edit form.</summary>
        public ConfigSchema ConfigSchema { get; }

        /// <summary>Declared metrics. Empty in Phase 1; non-empty for metric-emitting
        /// providers (e.g. the AI canary, #317).</summary>
        public IReadOnlyList<MetricDef> MetricDefs { get; }

        /// <summary>#317: the per-probe timeout the engine grants this provider. HTTP keeps
        /// the 10s default; the AI canary uses a longer ceiling (an LLM may legitimately
        /// be slower). The engine wraps the probe in this + a small backstop.</summary>
        public TimeSpan ProbeTimeout { get; }

        /// <summary>#335: one operator-facing sentence on what this provider does.
        /// Server-driven so UI surfaces never carry page-local provider prose.</summary>
        public string Description { get; }

        /// <summary>#335: pull (engine probes the target) vs push (target pings in).</summary>
        public ProbeDirection Direction { get; }

        /// <summary>#342: batch-paste capability. The <see cref="ConfigField.Key"/> a
        /// pasted line maps into (<c>url</c> for http, <c>baseUrl</c> for ai) — the one
        /// field the "Batch add" dialog fills per-target instead of asking once. Null ⇒
        /// this provider has no pasteable target (e.g. heartbeat, a push dead-man's-switch)
        /// and is excluded from the batch dialog. This is serializable metadata only; the
        /// parse/canonicalize behaviour lives on <see cref="ICheckProvider.TryParseBatchTarget"/>.</summary>
        public string? BatchTargetField { get; }
    }
}
