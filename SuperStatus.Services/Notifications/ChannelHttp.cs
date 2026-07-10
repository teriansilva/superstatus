using System.Text;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Entities;
using SuperStatus.Services.Http;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// #343 Phase 5: shared JSON-POST wire core for the chat-channel providers. Uses the
/// existing named "status-webhook" <see cref="System.Net.Http.HttpClient"/> (pooled +
/// timeout-bounded); maps the HTTP result to a <see cref="NotificationSendResult"/>; an
/// endpoint that throws is contained as a Failed result (the type only is logged, never
/// the secret URL/token). <paramref name="target"/> is the safe audit label (never a
/// secret) recorded on the delivery row.
/// </summary>
internal static class ChannelHttp
{
    public static async Task<NotificationSendResult> PostJsonAsync(
        IHttpClientFactory httpClientFactory, string url, string jsonBody, string target, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(StatusCheckHttpClients.Webhook);
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content, cancellationToken);
            return response.IsSuccessStatusCode
                ? NotificationSendResult.Sent(target)
                : NotificationSendResult.Failed(target, Sanitize($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Target} channel POST failed ({ExceptionType}).", target, ex.GetType().Name);
            return NotificationSendResult.Failed(target, $"{target} request failed");
        }
    }

    private static string Sanitize(string? message)
    {
        var m = (message ?? "send failed").Trim();
        return m.Length > AlertDeliveryLog.MaxErrorMessageLength ? m[..AlertDeliveryLog.MaxErrorMessageLength] : m;
    }
}
