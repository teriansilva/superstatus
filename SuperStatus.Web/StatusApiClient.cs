using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;
using Microsoft.AspNetCore.Components.Authorization;

namespace SuperStatus.Web;

public class StatusApiClient
{
    private readonly HttpClient _anon;
    private readonly HttpClient _auth;
    private readonly AuthenticationStateProvider? _authStateProvider;

    public StatusApiClient(IHttpClientFactory factory, AuthenticationStateProvider? authStateProvider = null)
    {
        _anon = factory.CreateClient("apiservice-anon");
        _auth = factory.CreateClient("apiservice-auth");
        _authStateProvider = authStateProvider;
    }

    private async Task<HttpClient> GetHttpClientAsync()
    {
        // If we have an auth state provider, check if user is authenticated
        if (_authStateProvider != null)
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            if (authState.User.Identity?.IsAuthenticated == true)
            {
                return _auth; // Use authenticated client (will send access token)
            }
        }

        return _anon; // Use anonymous client (no token)
    }

    public async Task<IPagedResult<StatusCheckViewModel>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        IPagedResult<StatusCheckViewModel> statusCheckSet = new PagedResult<StatusCheckViewModel>();
        var client = await GetHttpClientAsync();
        return await client.GetFromJsonAsync<PagedResult<StatusCheckViewModel>>("/statuscheck", cancellationToken) ?? statusCheckSet;
    }

    public async Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusData(long statusCheckId, CancellationToken cancellationToken = default)
    {
        List<HistoricalStatusDataOverviewChartViewModel>? historicalStatusSet = new List<HistoricalStatusDataOverviewChartViewModel>();
        var client = await GetHttpClientAsync();

        await foreach (var historicalStatus in client.GetFromJsonAsAsyncEnumerable<HistoricalStatusDataOverviewChartViewModel>($"/statuscheck/gethistoricaldata/{statusCheckId}", cancellationToken))
        {
            if (historicalStatus is not null)
            {
                historicalStatusSet.Add(historicalStatus);
            }
        }
        return historicalStatusSet;
    }

    public async Task UpdateOrAddStatusCheck(StatusCheckViewModelBase statusCheckToSave, CancellationToken cancellationToken = default)
    {
        // Always use authenticated client for this endpoint
        using var response = await _auth.PostAsJsonAsync("/statuscheck/edit", statusCheckToSave, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to save status check. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
    }
}
