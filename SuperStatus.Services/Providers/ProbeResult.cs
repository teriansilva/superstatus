using SuperStatus.Data.Constants;

namespace SuperStatus.Services.Providers
{
    /// <summary>Normalized health verdict, independent of any one provider's protocol.</summary>
    public enum ProbeOutcome
    {
        Up = 0,
        Degraded = 1,
        Down = 2,
    }

    /// <summary>
    /// Epic #271 / #312 Phase 1. The normalized outcome of one probe. A provider fills
    /// in the <b>protocol-level</b> verdict — reachability + its own failure class
    /// (HTTP: <see cref="FailType.Unreachable"/> / <see cref="FailType.StatusCode"/> /
    /// <see cref="FailType.NoFail"/>) — plus latency and any provider-specific extras.
    /// The engine then applies cross-cutting concerns (the linked-SLA slow threshold,
    /// which can upgrade a healthy result to <see cref="FailType.ResponseTime"/>) and
    /// adapts the result back onto the existing <c>HistoricalStatusData</c> fields, so
    /// every downstream read is byte-for-byte unchanged.
    /// </summary>
    public sealed class ProbeResult
    {
        /// <summary>Provider's failure classification. Providers set
        /// <see cref="FailType.Unreachable"/> / <see cref="FailType.StatusCode"/> /
        /// <see cref="FailType.NoFail"/>; the engine may refine a <see cref="FailType.NoFail"/>
        /// to <see cref="FailType.ResponseTime"/> via the latency SLO.</summary>
        public FailType FailType { get; init; } = FailType.NoFail;

        /// <summary>Measured latency in ms. 0 when the target was unreachable
        /// (preserves the existing HTTP behavior exactly).</summary>
        public long LatencyMs { get; init; }

        /// <summary>True unless the probe could not complete a request at all
        /// (maps to the existing <c>CheckFailed</c> flag).</summary>
        public bool Reachable { get; init; } = true;

        /// <summary>HTTP-specific: the observed status code (0 when unreachable). Lives
        /// here rather than in the generic core so non-HTTP providers don't carry it.</summary>
        public int HttpStatusCode { get; init; }

        /// <summary>Declared metrics for this tick. Null in Phase 1 (no provider emits any).</summary>
        public string? MetricsJson { get; init; }

        /// <summary>Optional human-readable note (e.g. the exception summary on failure).
        /// <b>Not safe to show a client</b> — see <see cref="PublicMessage"/>.</summary>
        public string? Message { get; init; }

        /// <summary>
        /// #433: a message the provider asserts is safe to hand to an operator over the API.
        /// <para>
        /// <see cref="Message"/> is a diagnostic channel and carries provider exception text —
        /// <c>HttpCheckProvider</c> returns <c>ex.Message</c> on transport failure, and so does
        /// the engine's containment fallback. .NET transport exceptions routinely embed the
        /// full request URL, including any userinfo or query-string credential, so forwarding
        /// <see cref="Message"/> to a client would leak them. It is therefore <b>opt-in</b>: a
        /// provider sets this only for text it synthesised itself (the AI canary's threshold
        /// and expected-content notes), never for anything derived from an exception or from an
        /// upstream response body.
        /// </para>
        /// Null ⇒ the API derives a message from <see cref="FailType"/> and the status class
        /// instead. Nothing here changes what the engine logs or stores; it reads
        /// <see cref="Message"/> exactly as before.
        /// </summary>
        public string? PublicMessage { get; init; }

        /// <summary>Normalized up/degraded/down view, derived from <see cref="FailType"/>
        /// (mirrors <c>PublicStatusApi.MapStateLabel</c>).</summary>
        public ProbeOutcome Outcome => FailType switch
        {
            FailType.NoFail => ProbeOutcome.Up,
            FailType.ResponseTime => ProbeOutcome.Degraded,
            _ => ProbeOutcome.Down,
        };

        /// <summary>A reachable HTTP response with the observed status + latency.</summary>
        public static ProbeResult Http(FailType failType, long latencyMs, int httpStatusCode, string? message = null) => new()
        {
            FailType = failType,
            LatencyMs = latencyMs,
            HttpStatusCode = httpStatusCode,
            Reachable = true,
            Message = message,
        };

        /// <summary>An unreachable target (transport failure / timeout / provider throw):
        /// the existing "checkFailed" shape — status 0, latency 0, <see cref="FailType.Unreachable"/>.</summary>
        public static ProbeResult Unreachable(string? message = null) => new()
        {
            FailType = FailType.Unreachable,
            LatencyMs = 0,
            HttpStatusCode = 0,
            Reachable = false,
            Message = message,
        };
    }
}
