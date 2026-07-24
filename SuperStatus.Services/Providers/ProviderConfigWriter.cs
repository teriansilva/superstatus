using System.Globalization;
using System.Text.Json.Nodes;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. Builds a provider's stored <c>ConfigJson</c> from the
    /// incoming (schema-keyed) form values, applying the <b>secret rule</b>: a blank
    /// <see cref="ConfigFieldKind.Secret"/> value <b>preserves</b> the previously-stored
    /// secret ("leave blank to keep" / write-only), while a non-blank one replaces it.
    /// The result is stamped with the schema version. This is the single place
    /// credential-bearing config is written, so no provider can clobber or leak a secret
    /// by default.
    ///
    /// <para>A blank/absent <b>optional</b> non-secret field is <b>omitted</b>, not stored
    /// as an empty string: <see cref="ConfigSchema.Validate"/> treats an absent field as
    /// "unset" (and an absent <i>required</i> field as "missing required 'X'"). Storing
    /// <c>""</c> made a Number/Select slot fail its own type check on load — disabling a
    /// check the user had filled in correctly (#388).</para>
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
                bool blank = string.IsNullOrWhiteSpace(value);

                if (field.Kind == ConfigFieldKind.Secret)
                {
                    if (blank)
                    {
                        // "Leave blank to keep": preserve the stored secret if there is
                        // one; otherwise omit the key.
                        if (existing.TryGetPropertyValue(field.Key, out var prev) && prev is not null)
                        {
                            result[field.Key] = prev.DeepClone();
                        }
                    }
                    else
                    {
                        result[field.Key] = JsonValue.Create(value);
                    }
                    continue;
                }

                if (field.Kind == ConfigFieldKind.Bool)
                {
                    // A checkbox always resolves to a definite value; blank/absent is false.
                    result[field.Key] = JsonValue.Create(
                        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                // Text / Number / Json / Select: omit a blank/absent optional value rather
                // than storing "" (the validator would reject an empty Number/Select). A
                // blank *required* field surfaces as "missing required 'X'" on validate.
                if (blank) continue;

                result[field.Key] = Coerce(field.Kind, value!);
            }

            return result.ToJsonString();
        }

        /// <summary>Coerce a non-blank value to the field's real JSON type. Numbers accept
        /// both integers and decimals (InvariantCulture) so a value the config model reads
        /// as a <c>double</c> — e.g. min tokens/sec — isn't stored as a string the schema
        /// validator then rejects (#388). A non-numeric value is kept as-is so
        /// <see cref="ConfigSchema.Validate"/> reports "'X' must be a number".</summary>
        private static JsonNode Coerce(ConfigFieldKind kind, string value)
        {
            if (kind == ConfigFieldKind.Number)
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return JsonValue.Create(l);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return JsonValue.Create(d);
            }
            return JsonValue.Create(value);
        }

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
