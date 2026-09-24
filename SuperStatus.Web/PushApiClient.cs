using System.Net.Http.Json;

namespace SuperStatus.Web;

/// <summary>
/// Issue #241 Phase C: typed client over the Web Push API (the api-service
/// <c>/api/push/*</c> endpoints). Like <see cref="SettingsApiClient"/>, it rides the
/// machine ("apiservice") client-credentials token so the authorized POSTs are
/// satisfied without a per-user bearer; the operator-only gate is the [Authorize]
/// admin surface the enable button lives on. <see cref="GetVapidKeyAsync"/> degrades
/// to null on transport failure so the button can show a calm error instead of
/// crashing the circuit.
/// </summary>
public class PushApiClient(HttpClient httpClient)
{
    /// <summary>The server's VAPID public key (generated once on first request),
    /// or null if it couldn't be fetched.</summary>
    public async Task<string?> GetVapidKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var body = await httpClient.GetFromJsonAsync<VapidKeyResponse>("/api/push/vapid-key", cancellationToken);
            return body?.Key;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Register (upsert) a browser push subscription. Throws on non-success
    /// so the caller can surface the failure.</summary>
    public async Task SubscribeAsync(string endpoint, string p256dh, string auth, string? userAgent, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/push/subscribe", new { endpoint, p256dh, auth, userAgent }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Drop a browser push subscription by endpoint. Throws on non-success.</summary>
    public async Task UnsubscribeAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/push/unsubscribe", new { endpoint }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Current subscribed-device count for Plugins -> Browser Push.
    /// Returns null on transport failure so the catalogue can degrade honestly.</summary>
    public async Task<int?> GetSubscriptionCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var body = await httpClient.GetFromJsonAsync<PushSubscriptionCountResponse>(
                "/api/push/subscriptions/count", cancellationToken);
            return body?.Count;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record VapidKeyResponse(string Key);
    private sealed record PushSubscriptionCountResponse(int Count);
}
