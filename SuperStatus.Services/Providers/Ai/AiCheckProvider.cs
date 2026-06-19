using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Services.Http;

namespace SuperStatus.Services.Providers.Ai
{
    /// <summary>
    /// Epic #271 / #317 Phase 2a. The headline metric-emitting provider: an
    /// OpenAI-compatible AI/LLM canary. It POSTs a canary prompt to
    /// <c>{baseUrl}/chat/completions</c> (streaming for a real TTFT), asserts the response
    /// <b>content</b>, and emits declared metrics — <c>ttft_ms</c>, <c>tokens_per_sec</c>,
    /// <c>latency_ms</c>, <c>completion_tokens</c>. Outcomes map onto the existing
    /// <see cref="FailType"/> so incidents / alerts / public API / rollups are unchanged.
    /// Reuses the OpenAI-compatible request shape already used by <c>IncidentDraftService</c>.
    /// </summary>
    public sealed class AiCheckProvider : ICheckProvider
    {
        public const string TypeId = "ai";
        public const int SchemaVersion = 1;

        // Declared metric keys (a provider may only emit metrics it declared).
        public const string MetricTtftMs = "ttft_ms";
        public const string MetricTokensPerSec = "tokens_per_sec";
        public const string MetricLatencyMs = "latency_ms";
        public const string MetricCompletionTokens = "completion_tokens";

        private const int MaxBufferedChars = 64 * 1024; // bound the accumulated response

        private static readonly ProviderDescriptor _descriptor = new(
            typeId: TypeId,
            displayName: "AI / LLM endpoint",
            icon: "sparkle",
            configSchema: new ConfigSchema(SchemaVersion, new ConfigField[]
            {
                new(AiCheckConfig.BaseUrlKey, "Base URL", ConfigFieldKind.Text, Required: true,
                    Help: "OpenAI-compatible base URL; the canary POSTs to {baseUrl}/chat/completions.",
                    Placeholder: "https://api.openai.com/v1"),
                new(AiCheckConfig.ModelKey, "Model", ConfigFieldKind.Text, Required: true, Placeholder: "gpt-4o-mini"),
                new(AiCheckConfig.ApiKeyKey, "API key", ConfigFieldKind.Secret, Required: false,
                    Help: "Sent as a Bearer token. Leave blank for an endpoint that needs none."),
                new(AiCheckConfig.PromptKey, "Canary prompt", ConfigFieldKind.Text, Required: true,
                    Placeholder: "Reply with the single word: pong"),
                new(AiCheckConfig.ExpectContainsKey, "Response must contain", ConfigFieldKind.Text, Required: true,
                    Help: "A substring the response must contain (case-insensitive), else the check is down.",
                    Placeholder: "pong"),
                new(AiCheckConfig.MaxTokensKey, "Max tokens", ConfigFieldKind.Number, Required: false, Placeholder: "32"),
                new(AiCheckConfig.TtftThresholdMsKey, "TTFT threshold (ms)", ConfigFieldKind.Number, Required: false,
                    Help: "Degraded if time-to-first-token exceeds this."),
                new(AiCheckConfig.MinTokensPerSecKey, "Min tokens/sec", ConfigFieldKind.Number, Required: false,
                    Help: "Degraded if throughput falls below this."),
            }),
            metricDefs: new MetricDef[]
            {
                new(MetricTtftMs, "TTFT", "ms", MetricKind.Gauge),
                new(MetricTokensPerSec, "Throughput", "tok/s", MetricKind.Gauge),
                new(MetricLatencyMs, "Latency", "ms", MetricKind.Gauge),
                new(MetricCompletionTokens, "Tokens", "tokens", MetricKind.Gauge),
            },
            // An LLM canary may legitimately be slower than an HTTP check.
            probeTimeout: TimeSpan.FromSeconds(30));

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AiCheckProvider>? _logger;

        public AiCheckProvider(IHttpClientFactory httpClientFactory, ILogger<AiCheckProvider>? logger = null)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public ProviderDescriptor Descriptor => _descriptor;

        public async Task<ProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken = default)
        {
            if (!AiCheckConfig.TryParse(context.ConfigJson, out var config))
            {
                return ProbeResult.Unreachable("invalid AI config");
            }

            var client = _httpClientFactory.CreateClient(StatusCheckHttpClients.AiCheck);
            string url = config.BaseUrl.TrimEnd('/') + "/chat/completions";

            var requestBody = new JsonObject
            {
                ["model"] = config.Model,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = config.Prompt }),
                ["stream"] = true,
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
            };
            if (config.MaxTokens is { } mt && mt > 0) requestBody["max_tokens"] = mt;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                    req.Headers.Authorization = new("Bearer", config.ApiKey);

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogInformation("AI canary {Title}: endpoint returned HTTP {Code}", context.CheckTitle, (int)resp.StatusCode);
                    return ProbeResult.Unreachable($"HTTP {(int)resp.StatusCode}");
                }

                var (content, ttftMs, completionTokens) = await ReadStreamAsync(resp, stopwatch, cancellationToken);
                long latencyMs = stopwatch.ElapsedMilliseconds;

                // tokens/sec over the generation window (after the first token), guarded.
                double genSeconds = Math.Max(0.001, (latencyMs - (ttftMs < 0 ? 0 : ttftMs)) / 1000.0);
                double tokensPerSec = completionTokens > 0 ? completionTokens / genSeconds : 0;
                double effectiveTtft = ttftMs < 0 ? latencyMs : ttftMs; // no streamed token → first byte ≈ latency

                var metrics = new JsonObject
                {
                    [MetricTtftMs] = effectiveTtft,
                    [MetricTokensPerSec] = Math.Round(tokensPerSec, 2),
                    [MetricLatencyMs] = latencyMs,
                    [MetricCompletionTokens] = completionTokens,
                };

                FailType failType = Classify(config, content, effectiveTtft, tokensPerSec, out string? message);
                return new ProbeResult
                {
                    FailType = failType,
                    LatencyMs = latencyMs,
                    Reachable = true,
                    MetricsJson = metrics.ToJsonString(),
                    Message = message,
                };
            }
            catch (Exception ex)
            {
                // Scrub like IncidentDraftService: log the exception TYPE only — never its
                // message or the URL/key — so a provider error string can't reach logs.
                // The returned Message is diagnostic-only (not persisted, not in the public
                // API), but keep it generic too.
                _logger?.LogInformation("AI canary failed for {Title} ({ExType})", context.CheckTitle, ex.GetType().Name);
                return ProbeResult.Unreachable("request failed");
            }
        }

        // OpenAI-compatible classification onto the existing FailType vocabulary so all
        // downstream (incidents/alerts/public API) is unchanged.
        private static FailType Classify(AiCheckConfig config, string content, double ttftMs, double tokensPerSec, out string? message)
        {
            message = null;
            if (string.IsNullOrEmpty(content) ||
                (!string.IsNullOrEmpty(config.ExpectContains)
                 && content.IndexOf(config.ExpectContains, StringComparison.OrdinalIgnoreCase) < 0))
            {
                message = "response did not contain the expected text";
                return FailType.StatusCode; // a reachable-but-bad response ⇒ down
            }
            if (config.TtftThresholdMs is { } tt && ttftMs > tt)
            {
                message = $"TTFT {ttftMs:0}ms exceeded threshold {tt:0}ms";
                return FailType.ResponseTime; // degraded
            }
            if (config.MinTokensPerSec is { } mtps && tokensPerSec > 0 && tokensPerSec < mtps)
            {
                message = $"throughput {tokensPerSec:0.0} tok/s below {mtps:0.0}";
                return FailType.ResponseTime; // degraded
            }
            return FailType.NoFail;
        }

        /// <summary>
        /// Read the (streaming) response: capture TTFT at the first content token,
        /// accumulate content, and read usage. Tolerates a non-streaming endpoint by
        /// falling back to a plain chat-completion parse of the buffered body.
        /// </summary>
        private static async Task<(string content, long ttftMs, int completionTokens)> ReadStreamAsync(
            HttpResponseMessage resp, Stopwatch stopwatch, CancellationToken ct)
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var streamed = new StringBuilder();
            var buffered = new StringBuilder();
            long ttftMs = -1;
            int completionTokens = 0;

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (buffered.Length < MaxBufferedChars) buffered.Append(line).Append('\n');
                if (line.Length == 0) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                        && usage.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt32(out var n))
                    {
                        completionTokens = n;
                    }
                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var piece = c.GetString();
                        if (!string.IsNullOrEmpty(piece))
                        {
                            if (ttftMs < 0) ttftMs = stopwatch.ElapsedMilliseconds;
                            streamed.Append(piece);
                        }
                    }
                }
                catch (JsonException) { /* tolerate a malformed chunk */ }
            }

            if (streamed.Length > 0)
            {
                if (completionTokens == 0) completionTokens = EstimateTokens(streamed.Length);
                return (streamed.ToString(), ttftMs, completionTokens);
            }

            // Non-streaming fallback: parse the buffered body as a plain completion.
            string content = ParseNonStreaming(buffered.ToString());
            if (completionTokens == 0 && content.Length > 0) completionTokens = EstimateTokens(content.Length);
            return (content, -1, completionTokens); // ttft unknown ⇒ caller uses latency
        }

        private static string ParseNonStreaming(string body)
        {
            var trimmed = body.Trim();
            if (trimmed.Length == 0) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    return c.GetString() ?? string.Empty;
                }
            }
            catch (JsonException) { }
            return string.Empty;
        }

        // Rough token estimate when the endpoint doesn't report usage (~4 chars/token).
        private static int EstimateTokens(int chars) => Math.Max(1, chars / 4);
    }
}
