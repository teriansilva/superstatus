using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Services.Http;

namespace SuperStatus.Services.Providers.Http
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. The first provider: a behavior-preserving extraction of
    /// the pre-#312 <c>StatusCheckService.ExecuteStatusCheck</c> HTTP logic. Same named
    /// <c>IHttpClientFactory</c> client (<see cref="StatusCheckHttpClients.StatusCheck"/>),
    /// same 10s timeout, same redirect handling, same exception→<see cref="FailType.Unreachable"/>
    /// mapping, and the same status-code comparison.
    /// <para>
    /// It classifies only the <b>protocol-level</b> outcome — reachability and the
    /// <c>ExpectedStatusCode</c> match. The latency SLO (slow → <see cref="FailType.ResponseTime"/>)
    /// is a cross-cutting concern the engine applies from the linked SLA, exactly as before.
    /// </para>
    /// </summary>
    public sealed class HttpCheckProvider : ICheckProvider
    {
        public const string TypeId = "http";
        public const int SchemaVersion = 1;

        private static readonly ProviderDescriptor _descriptor = new(
            typeId: TypeId,
            displayName: "HTTP(S)",
            icon: "link",
            configSchema: new ConfigSchema(SchemaVersion, new ConfigField[]
            {
                new(
                    Key: HttpCheckConfig.UrlKey,
                    Label: "URL",
                    Kind: ConfigFieldKind.Text,
                    Required: true,
                    Help: "The endpoint to probe with a GET request.",
                    Placeholder: "https://api.example.com/health"),
                // A number (not a closed select): an existing check may expect ANY
                // status code (e.g. 500/503/418). Constraining it to a short list would
                // disable those checks on migration — parity demands the full range.
                new(
                    Key: HttpCheckConfig.ExpectedStatusCodeKey,
                    Label: "Expected status",
                    Kind: ConfigFieldKind.Number,
                    Required: true,
                    Help: "The HTTP status code that counts as healthy (e.g. 200, 204, 301, 401).",
                    Placeholder: "200"),
            }));

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<HttpCheckProvider>? _logger;

        public HttpCheckProvider(IHttpClientFactory httpClientFactory, ILogger<HttpCheckProvider>? logger = null)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public ProviderDescriptor Descriptor => _descriptor;

        public async Task<ProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken = default)
        {
            if (!HttpCheckConfig.TryParse(context.ConfigJson, out var config) || string.IsNullOrWhiteSpace(config.Url))
            {
                // Defensive: the disable-with-reason gate validates config before a probe
                // is dispatched, so a probe should never reach here with bad config.
                return ProbeResult.Unreachable("invalid HTTP config");
            }

            // Issue #77: pooled, instrumented, 10s-timeout client from the factory.
            // NOT disposed — the factory owns the handler lifetime.
            var client = _httpClientFactory.CreateClient(StatusCheckHttpClients.StatusCheck);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = await client.GetAsync(config.Url, cancellationToken);
                long latencyMs = stopwatch.ElapsedMilliseconds;
                int httpStatusCode = (int)response.StatusCode;

                // Protocol-level classification only — the engine applies the latency SLO.
                FailType failType = httpStatusCode != config.ExpectedStatusCode
                    ? FailType.StatusCode
                    : FailType.NoFail;

                return ProbeResult.Http(failType, latencyMs, httpStatusCode);
            }
            catch (Exception ex)
            {
                // #85: an unreachable target is an expected, high-frequency event for a
                // status monitor — log at Information with the URL in a structured field,
                // exactly as the pre-#312 path did.
                _logger?.LogInformation(ex, "Failed to execute status check on {Url}", config.Url);
                return ProbeResult.Unreachable(ex.Message);
            }
        }
    }
}
