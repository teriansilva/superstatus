using Microsoft.AspNetCore.Authentication;
using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;
using System.Net.Http.Headers;

namespace SuperStatus.Web;

public class IncidentApiClient(HttpClient httpClient)
{
    public async Task<IDictionary<DateTime, List<IncidentViewModel>>> GetIncidentsAsync(CancellationToken cancellationToken = default)
    {
        IDictionary<DateTime, List<IncidentViewModel>> incidentSet = new Dictionary<DateTime, List<IncidentViewModel>>();

        return await httpClient.GetFromJsonAsync<IDictionary<DateTime, List<IncidentViewModel>>>("/incidents", cancellationToken) ?? incidentSet;

    }

    public async Task UpdateOrAddIncident(IncidentViewModel incidentToSave, CancellationToken cancellationToken = default)
    {

        using var response = await httpClient.PostAsJsonAsync("/incidents/edit", incidentToSave, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to save incident. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
    }
}
