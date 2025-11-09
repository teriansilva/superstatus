using Microsoft.AspNetCore.Authentication;
using SuperStatus.Data.Entities;
using System.Net.Http.Headers;

namespace SuperStatus.Web;

public class ConfigurationApiClient(HttpClient httpClient, IHttpContextAccessor accessor)
{
    public async Task<Configuration?> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await accessor.HttpContext?.GetTokenAsync("access_token");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await httpClient.GetFromJsonAsync<Configuration>("/admin/configuration", cancellationToken);
    }

    public async Task UpdateConfigurationAsync(Configuration configuration, CancellationToken cancellationToken = default)
    {
        var accessToken = await accessor.HttpContext?.GetTokenAsync("access_token");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.PostAsJsonAsync("/admin/configuration", configuration, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to update configuration. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
    }
}