using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using SuperStatus.Services.Http;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// #343 Phase 5: shared JSON wire core for the chat-channel providers. Uses the
/// existing named "status-webhook" <see cref="System.Net.Http.HttpClient"/> (pooled +
/// timeout-bounded); maps the HTTP result to a <see cref="NotificationSendResult"/>; an
/// endpoint that throws is contained as a Failed result (the type only is logged, never
/// the secret URL/token). <paramref name="target"/> is the safe audit label (never a
/// secret) recorded on the delivery row.
/// </summary>
internal static class ChannelHttp
{
    /// <summary>The Slack / Discord / Telegram shape: an unauthenticated JSON POST (the
    /// credential is carried by the URL itself).</summary>
    public static Task<NotificationSendResult> PostJsonAsync(
        IHttpClientFactory httpClientFactory, string url, string jsonBody, string target, ILogger logger, CancellationToken cancellationToken)
        => SendJsonAsync(httpClientFactory, HttpMethod.Post, url, jsonBody, target, bearerToken: null, logger, cancellationToken);

    /// <summary>
    /// #430: the general form — an arbitrary <paramref name="method"/> plus an optional
    /// bearer credential, for channels whose API is not a bare POST (Matrix sends a
    /// <c>PUT</c> authenticated with an <c>Authorization</c> header). The token is set on the
    /// request header only: it never enters the URL, the audit target, or a log line.
    /// </summary>
    public static async Task<NotificationSendResult> SendJsonAsync(
        IHttpClientFactory httpClientFactory, HttpMethod method, string url, string jsonBody, string target,
        string? bearerToken, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(StatusCheckHttpClients.Webhook);
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(bearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            var response = await client.SendAsync(request, cancellationToken);
            // Only the numeric status code — never the reason phrase. That phrase is
            // remote-controlled text, and this Detail is persisted verbatim to
            // AlertDeliveryLog.ErrorMessage (AlertEvaluator) and shown in the admin alert
            // log, so an endpoint could echo the bearer credential we just sent it straight
            // into the audit database. The code is the actionable part anyway; the endpoint's
            // own error body (e.g. Matrix's errcode) is deliberately not persisted either.
            return response.IsSuccessStatusCode
                ? NotificationSendResult.Sent(target)
                : NotificationSendResult.Failed(target, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Target} channel {Method} failed ({ExceptionType}).", target, method.Method, ex.GetType().Name);
            return NotificationSendResult.Failed(target, $"{target} request failed");
        }
    }
}
