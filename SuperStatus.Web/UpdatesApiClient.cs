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
}
