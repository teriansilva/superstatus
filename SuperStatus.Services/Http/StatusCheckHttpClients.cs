namespace SuperStatus.Services.Http
{
    /// <summary>
    /// Issue #77. Named <see cref="System.Net.Http.IHttpClientFactory"/> client
    /// identities for the status-check pipeline. Centralised so the registration
    /// (<c>AddStatusCheckHttpClients</c>) and the consumers
    /// (<c>StatusCheckService</c>) can never drift on a magic string.
    ///
    /// Both clients are created from the factory so they share a pooled
    /// <c>SocketsHttpHandler</c> (no socket exhaustion under fan-out) and are
    /// visible to <c>AddHttpClientInstrumentation</c> in OTel — neither was true
    /// of the raw <c>new HttpClient()</c> calls this replaces.
    /// </summary>
    public static class StatusCheckHttpClients
    {
        /// <summary>Outbound client for probing a monitored endpoint.</summary>
        public const string StatusCheck = "status-check";

        /// <summary>Outbound client for firing on-error webhooks.</summary>
        public const string Webhook = "status-webhook";

        /// <summary>Issue #168: outbound client for the OpenAI-compatible incident
        /// draft endpoint. Unlike the two above it has NO fixed client timeout —
        /// the draft service applies the operator-configured per-request timeout
        /// (AiTimeoutSeconds) via a linked CancellationTokenSource instead.</summary>
        public const string AiIncident = "ai-incident";

        /// <summary>
        /// Uniform request ceiling for both clients. A single hung target must
        /// not keep the surrounding scope's DbContext/Npgsql connection pinned
        /// for the framework default of 100 s — that is the exhaustion #71
        /// fixed and #78 must not reintroduce. A timeout surfaces as
        /// <see cref="System.Threading.Tasks.TaskCanceledException"/>, which the
        /// callers classify as unreachable/timeout exactly as before.
        /// </summary>
        public const int TimeoutSeconds = 10;
    }
}
