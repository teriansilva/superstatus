using System.Text.Json;

namespace SuperStatus.Services.Providers.Ai
{
    /// <summary>
    /// Epic #271 / #317 Phase 2a. Typed config for the AI/LLM canary provider, parsed
    /// from a check's <c>ConfigJson</c>. Validation against the schema (required fields,
    /// secret handling) happens in the Phase-1 gate before a probe runs.
    /// </summary>
    public sealed class AiCheckConfig
    {
        public const string BaseUrlKey = "baseUrl";
        public const string ModelKey = "model";
        public const string ApiKeyKey = "apiKey";
        public const string PromptKey = "prompt";
        public const string ExpectContainsKey = "expectContains";
        public const string MaxTokensKey = "maxTokens";
        public const string TtftThresholdMsKey = "ttftThresholdMs";
        public const string MinTokensPerSecKey = "minTokensPerSec";

        public string BaseUrl { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string? ApiKey { get; init; }
        public string Prompt { get; init; } = string.Empty;
        public string ExpectContains { get; init; } = string.Empty;
        public int? MaxTokens { get; init; }
        public double? TtftThresholdMs { get; init; }
        public double? MinTokensPerSec { get; init; }

        public static bool TryParse(string? configJson, out AiCheckConfig config)
        {
            config = new AiCheckConfig();
            if (string.IsNullOrWhiteSpace(configJson)) return false;
            try
            {
                using var doc = JsonDocument.Parse(configJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                config = new AiCheckConfig
                {
                    BaseUrl = Str(root, BaseUrlKey),
                    Model = Str(root, ModelKey),
                    ApiKey = NullableStr(root, ApiKeyKey),
                    Prompt = Str(root, PromptKey),
                    ExpectContains = Str(root, ExpectContainsKey),
                    MaxTokens = Int(root, MaxTokensKey),
                    TtftThresholdMs = Dbl(root, TtftThresholdMsKey),
                    MinTokensPerSec = Dbl(root, MinTokensPerSecKey),
                };
                return !string.IsNullOrWhiteSpace(config.BaseUrl) && !string.IsNullOrWhiteSpace(config.Model);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string Str(JsonElement root, string key) =>
            root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;

        private static string? NullableStr(JsonElement root, string key)
        {
            var s = Str(root, key);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static int? Int(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        private static double? Dbl(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
            return null;
        }
    }
}
