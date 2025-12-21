using Microsoft.AspNetCore.Authentication;
using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;
using System.Net.Http.Headers;

namespace SuperStatus.Web;

public class IncidentApiClient
{
    private readonly HttpClient _anon;
    private readonly HttpClient _auth;

    public IncidentApiClient(IHttpClientFactory factory)
    {
        _anon = factory.CreateClient("apiservice-anon");
        _auth = factory.CreateClient("apiservice-auth");
    }

    public async Task<IDictionary<DateTime, List<IncidentViewModel>>> GetIncidentsAsync(CancellationToken cancellationToken = default)
    {
        IDictionary<DateTime, List<IncidentViewModel>> incidentSet = new Dictionary<DateTime, List<IncidentViewModel>>();

        // Use anonymous client - this endpoint is public
        return await _anon.GetFromJsonAsync<IDictionary<DateTime, List<IncidentViewModel>>>("/incidents", cancellationToken) ?? incidentSet;
    }

    public async Task UpdateOrAddIncident(IncidentViewModel incidentToSave, CancellationToken cancellationToken = default)
    {
        // Use authenticated client - this endpoint requires authorization
        using var response = await _auth.PostAsJsonAsync("/incidents/edit", incidentToSave, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to save incident. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
    }
}
