using Microsoft.AspNetCore.Authentication;
using SuperStatus.Data.Entities;
using System.Net.Http.Headers;

namespace SuperStatus.Web;

public class ConfigurationApiClient
{
    private readonly HttpClient _anon;
    private readonly HttpClient _auth;

    public ConfigurationApiClient(IHttpClientFactory factory, IHttpContextAccessor accessor)
    {
        _anon = factory.CreateClient("apiservice-anon");
        _auth = factory.CreateClient("apiservice-auth");
    }

    public async Task<Configuration?> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        // Use anonymous client - this endpoint is now public
        return await _anon.GetFromJsonAsync<Configuration>("/configuration", cancellationToken);
    }

    public async Task UpdateConfigurationAsync(Configuration configuration, CancellationToken cancellationToken = default)
    {
        // Use authenticated client - this endpoint requires authorization
        using var response = await _auth.PostAsJsonAsync("/admin/configuration", configuration, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to update configuration. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
    }
}