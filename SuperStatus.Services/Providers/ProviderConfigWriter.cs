using System.Text.Json.Nodes;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. Builds a provider's stored <c>ConfigJson</c> from the
    /// incoming (schema-keyed) form values, applying the <b>secret rule</b>: a blank
    /// <see cref="ConfigFieldKind.Secret"/> value <b>preserves</b> the previously-stored
    /// secret ("leave blank to keep" / write-only), while a non-blank one replaces it.
    /// Non-secret fields always take the incoming value. The result is stamped with the
    /// schema version. This is the single place credential-bearing config is written, so
    /// no provider can clobber or leak a secret by default. (HTTP has no secret field
    /// yet; the rule lands now with the vocabulary.)
    /// </summary>
    public static class ProviderConfigWriter
    {
        public static string Build(ConfigSchema schema, IReadOnlyDictionary<string, string> incoming, string? existingJson)
        {
            JsonObject existing = ParseObject(existingJson);
            var result = new JsonObject { [ConfigSchema.VersionKey] = schema.Version };

            foreach (var field in schema.Fields)
            {
                incoming.TryGetValue(field.Key, out var value);

                if (field.Kind == ConfigFieldKind.Secret && string.IsNullOrWhiteSpace(value))
                {
                    // Preserve the stored secret if there is one; otherwise omit the key.
                    if (existing.TryGetPropertyValue(field.Key, out var prev) && prev is not null)
                    {
                        result[field.Key] = prev.DeepClone();
                    }
                }
                else
                {
                    // Coerce to the field's real JSON type so a bool/number isn't stored
                    // as a string the schema validator would then reject on load.
                    result[field.Key] = Coerce(field.Kind, value);
                }
            }

            return result.ToJsonString();
        }

        private static JsonNode Coerce(ConfigFieldKind kind, string? value) => kind switch
        {
            ConfigFieldKind.Bool => JsonValue.Create(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)),
            ConfigFieldKind.Number when long.TryParse(value, out var n) => JsonValue.Create(n),
            _ => JsonValue.Create(value ?? string.Empty),
        };

        private static JsonObject ParseObject(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch
            {
                return new JsonObject();
            }
        }
    }
}
