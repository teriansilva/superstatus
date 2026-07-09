using System.Net.Http.Json;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Web;

/// <summary>
/// Issue #249 (epic #248): typed client over the update endpoints
/// (<c>/api/updates</c> read, <c>/api/updates/check</c> on-demand check). Rides
/// the machine ("apiservice") client-credentials token like the other clients.
/// Both calls degrade to <c>null</c> on transport failure so the panel can render
/// an honest "couldn't reach the API" state rather than crashing the circuit.
/// </summary>
public class UpdatesApiClient(HttpClient httpClient)
{
    public async Task<UpdateStatusViewModel?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<UpdateStatusViewModel>("/api/updates", cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<UpdateStatusViewModel?> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync("/api/updates/check", content: null, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<UpdateStatusViewModel>(cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Issue #311: ask the api to apply the update now (it triggers Watchtower's http-api).
    /// Returns the parsed <see cref="UpdateApplyResult"/> on any HTTP response — 202 carries
    /// <c>Accepted=true</c>, a 4xx/5xx carries <c>Accepted=false</c> + a calm error. A
    /// transport failure degrades to a generic, retryable error rather than throwing.
    /// </summary>
    public async Task<UpdateApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync("/api/updates/apply", content: null, cancellationToken);
            var parsed = await response.Content.ReadFromJsonAsync<UpdateApplyResult>(cancellationToken: cancellationToken);
            return parsed ?? new UpdateApplyResult
            {
                Accepted = response.IsSuccessStatusCode,
                Error = response.IsSuccessStatusCode ? null : "The update couldn't be started.",
            };
        }
        catch (Exception)
        {
            // The apply restarts the api, so the request itself may be cut off mid-flight;
            // surface a calm, retryable message rather than crashing the circuit.
            return new UpdateApplyResult { Accepted = false, Error = "Couldn't reach the API to start the update." };
        }
    }
}
