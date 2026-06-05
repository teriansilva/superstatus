using System.Net.Http.Json;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Web;

// Thin typed HTTP client for the Grid view (issue #11).
// Anonymous read like StatusApiClient — no bearer token needed.
public class GridApiClient(HttpClient httpClient)
{
    public async Task<List<GridBuildingViewModel>> GetBuildingsAsync(CancellationToken cancellationToken = default)
    {
        // Empty list on error is the right default: the renderer will draw
        // an empty city (sky + street, no buildings) rather than crash the
        // Blazor circuit. Mirrors the resilience approach in StatusApiClient.
        return await httpClient.GetFromJsonAsync<List<GridBuildingViewModel>>(
                   "/statuscheck/grid", cancellationToken)
               ?? new List<GridBuildingViewModel>();
    }
}
