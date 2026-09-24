using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperStatus.Services.Http;

namespace SuperStatus.Services.Providers.Ai
{
    /// <summary>
    /// #433. Asks an OpenAI-compatible endpoint which models it serves, so the check edit
    /// dialog can offer a list instead of a free-text box the operator has to guess into.
    ///
    /// <para><b>This talks to an operator-supplied host, so it is written defensively.</b> The
    /// response is read under a hard byte ceiling <i>while streaming</i> — capping the returned
    /// list would not stop us parsing a 500 MB body first — and ids are then trimmed, length-
    /// and count-capped, de-duplicated and sorted. Nothing from the upstream body is ever put
    /// into a returned message or a log line: failure text is synthesised from the HTTP status
    /// class alone, the same scrubbing rule <see cref="AiCheckProvider"/> applies.</para>
    ///
    /// <para>An endpoint with no <c>/models</c> route is a normal, supported outcome — not an
    /// error. It comes back as <c>Ok</c> with <c>SupportsListing = false</c> so the dialog can
    /// fall back to free text rather than trapping the operator behind a gate they cannot
    /// pass.</para>
    /// </summary>
    public interface IAiModelDiscovery
    {
        Task<AiModelDiscoveryResult> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken = default);
    }

    /// <param name="Ok">False only when we could not get a usable answer at all.</param>
    /// <param name="SupportsListing">True when the endpoint actually served a model list.</param>
    /// <param name="Models">Sorted, de-duplicated, bounded model ids. Empty unless <paramref name="SupportsListing"/>.</param>
    /// <param name="Message">Operator-facing text, synthesised here — never upstream content.</param>
    public sealed record AiModelDiscoveryResult(bool Ok, bool SupportsListing, IReadOnlyList<string> Models, string? Message);

    public sealed class AiModelDiscovery : IAiModelDiscovery
    {
        /// <summary>Hard ceiling on the bytes we will read from an operator-supplied endpoint.
        /// Enforced while streaming, before any parse — this is the bound that matters.</summary>
        public const int MaxResponseBytes = 512 * 1024;

        /// <summary>A model id longer than this is not a model id.</summary>
        public const int MaxModelIdLength = 200;

        /// <summary>Upper bound on ids returned to the client. The dialog stays typeable, so a
        /// cap is never a dead end.</summary>
        public const int MaxModels = 500;

        /// <summary>Discovery is interactive — an operator is waiting on it — so it gets a much
        /// shorter ceiling than the AI canary's 30 s probe budget.</summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AiModelDiscovery>? _logger;

        public AiModelDiscovery(IHttpClientFactory httpClientFactory, ILogger<AiModelDiscovery>? logger = null)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<AiModelDiscoveryResult> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) ||
                !Uri.TryCreate(baseUrl.TrimEnd('/') + "/models", UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return new(false, false, Array.Empty<string>(), "The base URL is not a valid http(s) URL.");
            }

            var client = _httpClientFactory.CreateClient(StatusCheckHttpClients.AiModels);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Authorization = new("Bearer", apiKey);

                // .WaitAsync is the part that actually contains a misbehaving transport. Both
                // the linked token and HttpClient.Timeout are *cooperative* — a handler that
                // never observes cancellation simply never completes the await, and because
                // this runs inside an authenticated request it would pin that endpoint's scope
                // (resolved DbContext and all) indefinitely. WaitAsync abandons the await when
                // the deadline passes, exactly as the dry-run path does.
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .WaitAsync(timeout.Token);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new(false, false, Array.Empty<string>(), "The endpoint rejected the API key.");

                // A missing /models route is a supported shape, not a failure: plenty of
                // OpenAI-compatible gateways serve only /chat/completions.
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
                    return NoListing();

                if (!response.IsSuccessStatusCode)
                    return new(false, false, Array.Empty<string>(), $"The endpoint returned HTTP {(int)response.StatusCode}.");

                // Same containment on the read: a stream that stalls mid-body is the same hazard
                // as a send that never returns.
                string body = await ReadBoundedAsync(response, timeout.Token).WaitAsync(timeout.Token);
                var models = ParseModelIds(body);

                // Reachable, answered 200, but nothing model-shaped came back (an HTML login
                // page, a proxy banner, a truncated body). Same operator affordance as a 404.
                return models.Count == 0 ? NoListing() : new(true, true, models, $"Connected — {models.Count} model{(models.Count == 1 ? "" : "s")}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;  // the caller went away; not our failure to report
            }
            catch (Exception ex)
            {
                // Log the exception TYPE only — its message can carry the URL, and the URL can
                // carry userinfo. Same rule as AiCheckProvider.
                _logger?.LogInformation("AI model discovery failed ({ExType})", ex.GetType().Name);
                return new(false, false, Array.Empty<string>(), "Could not reach the endpoint.");
            }
        }

        private static AiModelDiscoveryResult NoListing() =>
            new(true, false, Array.Empty<string>(), "Reachable, but this endpoint doesn't list models — type the model id.");

        /// <summary>Read at most <see cref="MaxResponseBytes"/>, streaming. A truncated body
        /// simply fails to parse and collapses to the "doesn't list models" outcome, which is
        /// the right answer for an endpoint sending us something that large.</summary>
        private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[8 * 1024];
            using var accumulated = new MemoryStream();

            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                int room = MaxResponseBytes - (int)accumulated.Length;
                if (room <= 0) break;
                accumulated.Write(buffer, 0, Math.Min(read, room));
                if (accumulated.Length >= MaxResponseBytes) break;
            }

            return System.Text.Encoding.UTF8.GetString(accumulated.ToArray());
        }

        /// <summary>
        /// Parse the OpenAI-compatible <c>{ "data": [ { "id": "…" } ] }</c> shape. Tolerates a
        /// bare array and a plain list of strings, because compatible gateways vary. Anything
        /// unparseable yields an empty list — the caller turns that into "doesn't list models"
        /// rather than an error, so a weird body degrades instead of blocking.
        /// </summary>
        public static IReadOnlyList<string> ParseModelIds(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(body);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }

            JsonElement list;
            if (root.ValueKind == JsonValueKind.Array)
            {
                list = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("data", out var data)
                     && data.ValueKind == JsonValueKind.Array)
            {
                list = data;
            }
            else
            {
                return Array.Empty<string>();
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in list.EnumerateArray())
            {
                string? id = entry.ValueKind switch
                {
                    JsonValueKind.String => entry.GetString(),
                    JsonValueKind.Object when entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        => idEl.GetString(),
                    _ => null,
                };

                if (string.IsNullOrWhiteSpace(id)) continue;
                id = id.Trim();
                if (id.Length > MaxModelIdLength) continue;   // not a model id; drop rather than truncate
                ids.Add(id);
                if (ids.Count >= MaxModels) break;
            }

            var result = ids.ToList();
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}
