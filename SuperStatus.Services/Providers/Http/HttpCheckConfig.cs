using System.Text.Json;
using System.Text.Json.Nodes;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Providers.Http
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. The HTTP provider's typed config — the two HTTP-specific
    /// fields (<see cref="Url"/>, <see cref="ExpectedStatusCode"/>) that pre-#312 lived
    /// as <c>StatusCheck.StatusCheckUrl</c> / <c>ExpectedStatusCode</c> columns. Stored in
    /// <c>StatusCheck.ConfigJson</c> (and kept in sync with the legacy columns on save, so
    /// old read consumers are unchanged in Phase 1).
    /// </summary>
    public sealed class HttpCheckConfig
    {
        public const string UrlKey = "url";
        public const string ExpectedStatusCodeKey = "expectedStatusCode";

        public HttpCheckConfig(string url, int expectedStatusCode)
        {
            Url = url;
            ExpectedStatusCode = expectedStatusCode;
        }

        public string Url { get; }
        public int ExpectedStatusCode { get; }

        /// <summary>
        /// Serialize to the canonical <c>ConfigJson</c> shape (stamped with the current
        /// schema version). Used by the save path and the Phase-1 column→config backfill.
        /// </summary>
        public static string ToJson(string? url, int expectedStatusCode, int schemaVersion)
        {
            var obj = new JsonObject
            {
                [ConfigSchema.VersionKey] = schemaVersion,
                [UrlKey] = url ?? string.Empty,
                [ExpectedStatusCodeKey] = expectedStatusCode,
            };
            return obj.ToJsonString();
        }

        /// <summary>
        /// Parse a stored <c>ConfigJson</c> into the typed config. Tolerant of an
        /// <see cref="ExpectedStatusCode"/> stored as a number or a numeric string (the
        /// edit form may post either). Validation against the schema happens earlier
        /// (the disable-with-reason gate); this assumes a validated document.
        /// </summary>
        public static bool TryParse(string? configJson, out HttpCheckConfig config)
        {
            config = new HttpCheckConfig(string.Empty, 0);
            if (string.IsNullOrWhiteSpace(configJson)) return false;

            try
            {
                using var doc = JsonDocument.Parse(configJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                string url = root.TryGetProperty(UrlKey, out var u) && u.ValueKind == JsonValueKind.String
                    ? (u.GetString() ?? string.Empty)
                    : string.Empty;

                int expected = 0;
                if (root.TryGetProperty(ExpectedStatusCodeKey, out var e))
                {
                    if (e.ValueKind == JsonValueKind.Number) e.TryGetInt32(out expected);
                    else if (e.ValueKind == JsonValueKind.String) int.TryParse(e.GetString(), out expected);
                }

                config = new HttpCheckConfig(url, expected);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
