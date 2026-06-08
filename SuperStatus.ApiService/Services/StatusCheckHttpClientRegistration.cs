using System.Net;
using SuperStatus.Services.Http;

namespace SuperStatus.Services
{
    /// <summary>
    /// Issue #77. Registers the two named status-check HTTP clients on the
    /// factory, replacing the raw <c>new HttpClient()</c> calls in
    /// <c>StatusCheckService</c>.
    /// </summary>
    public static class StatusCheckHttpClientRegistration
    {
        public static IServiceCollection AddStatusCheckHttpClients(this IServiceCollection services)
        {
            services.AddHttpClient(StatusCheckHttpClients.StatusCheck, ConfigureClient)
                    .ConfigurePrimaryHttpMessageHandler(CreateHandler);

            services.AddHttpClient(StatusCheckHttpClients.Webhook, ConfigureClient)
                    .ConfigurePrimaryHttpMessageHandler(CreateHandler);

            // #168: AI incident-draft client. No fixed client timeout — the draft
            // service enforces the operator-configured AiTimeoutSeconds per request
            // via a linked CancellationTokenSource (a fixed client timeout would
            // override it). Same pooled/instrumented handler as the others.
            services.AddHttpClient(StatusCheckHttpClients.AiIncident, c => c.Timeout = Timeout.InfiniteTimeSpan)
                    .ConfigurePrimaryHttpMessageHandler(CreateHandler);

            return services;
        }

        private static void ConfigureClient(HttpClient client)
        {
            client.Timeout = TimeSpan.FromSeconds(StatusCheckHttpClients.TimeoutSeconds);
        }

        private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
        {
            // Recycle pooled connections so a long-lived process doesn't pin
            // a stale DNS result for a monitored host that moved.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),

            // Behaviour preserved deliberately (Hermes review on #77): the
            // previous raw HttpClient followed redirects, so a check whose URL
            // 30x-redirects to a healthy 200 is recorded as healthy today.
            // Flipping this to false would silently start failing those checks
            // (the recorded status becomes the 30x, which != ExpectedStatusCode).
            // Tightening redirect policy is therefore left to its own issue;
            // this PR is a pure lifetime/timeout/instrumentation swap with no
            // behavioural change.
            AllowAutoRedirect = true,
        };
    }
}
