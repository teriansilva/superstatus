using System.Text.Json;

namespace SuperStatus.Services.Plugins
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. A provider's <b>versioned</b> configuration schema:
    /// a closed list of <see cref="ConfigField"/>s plus a <see cref="Version"/>. The
    /// edit dialog renders the form from the fields; stored <c>ConfigJson</c> is
    /// validated against the current schema on load (see <see cref="Validate"/>). A
    /// schema-version bump must ship a config migration or the row is rejected —
    /// there is no best-effort guess (a row that can't be validated calmly disables
    /// its check; it never silently probes with defaults).
    /// </summary>
    public sealed class ConfigSchema
    {
        /// <summary>Reserved <c>ConfigJson</c> key carrying the schema version a row
        /// was written against. Absent ⇒ treated as the provider's current version
        /// (e.g. a row the Phase-1 migration backfilled from the legacy columns).</summary>
        public const string VersionKey = "schemaVersion";

        public ConfigSchema(int version, IReadOnlyList<ConfigField> fields)
        {
            Version = version;
            Fields = fields;
        }

        /// <summary>Monotonic schema version. Bump on any breaking field change.</summary>
        public int Version { get; }

        /// <summary>The ordered field vocabulary the form renders and the provider reads.</summary>
        public IReadOnlyList<ConfigField> Fields { get; }

        /// <summary>
        /// Validate a stored <c>ConfigJson</c> against this schema. Returns
        /// <c>null</c> when valid, otherwise a calm, human-readable reason the check
        /// is disabled. Covers: malformed JSON, an incompatible (newer/unknown)
        /// schema version, and missing/ill-typed required fields. <see cref="ConfigFieldKind.Secret"/>
        /// presence is validated like any required field (a stored secret must be
        /// non-empty); the write-side "blank to keep" rule is applied earlier, on save.
        /// </summary>
        public string? Validate(string? configJson)
        {
            if (string.IsNullOrWhiteSpace(configJson))
            {
                // Empty config is only valid if nothing is required.
                var firstRequired = Fields.FirstOrDefault(f => f.Required);
                return firstRequired is null ? null : $"missing required '{firstRequired.Label}'";
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(configJson);
                // Clone so we can read after the document is disposed.
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return "configuration is not valid JSON";
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return "configuration is not a JSON object";
            }

            // Version compatibility. A row written by a NEWER schema than this build
            // knows about can't be safely interpreted — reject rather than guess.
            if (root.TryGetProperty(VersionKey, out var verEl)
                && verEl.ValueKind == JsonValueKind.Number
                && verEl.TryGetInt32(out var storedVersion)
                && storedVersion != Version)
            {
                return $"configuration was saved against schema v{storedVersion}, but this provider uses v{Version} (no migration)";
            }

            foreach (var field in Fields)
            {
                bool present = root.TryGetProperty(field.Key, out var value)
                               && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

                if (!present)
                {
                    if (field.Required) return $"missing required '{field.Label}'";
                    continue;
                }

                switch (field.Kind)
                {
                    case ConfigFieldKind.Text:
                    case ConfigFieldKind.Secret:
                        if (value.ValueKind != JsonValueKind.String)
                            return $"'{field.Label}' must be text";
                        if (field.Required && string.IsNullOrWhiteSpace(value.GetString()))
                            return $"missing required '{field.Label}'";
                        break;

                    case ConfigFieldKind.Number:
                        // Accept a JSON number, or a numeric string (the edit form may
                        // post numbers as strings).
                        if (value.ValueKind == JsonValueKind.Number) break;
                        if (value.ValueKind == JsonValueKind.String
                            && long.TryParse(value.GetString(), out _)) break;
                        return $"'{field.Label}' must be a number";

                    case ConfigFieldKind.Bool:
                        // A real JSON bool, or a "true"/"false" string (the form may post either).
                        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) break;
                        if (value.ValueKind == JsonValueKind.String
                            && bool.TryParse(value.GetString(), out _)) break;
                        return $"'{field.Label}' must be true or false";

                    case ConfigFieldKind.Select:
                        string? selected = value.ValueKind == JsonValueKind.String
                            ? value.GetString()
                            : value.ToString();
                        var allowed = field.Options ?? Array.Empty<ConfigSelectOption>();
                        if (allowed.Count > 0 && !allowed.Any(o => o.Value == selected))
                            return $"'{field.Label}' has an unsupported value";
                        break;
                }
            }

            return null;
        }
    }
}
