using Microsoft.AspNetCore.Authentication;
using SuperStatus.Data.ViewModels;
using System.Net.Http.Headers;

namespace SuperStatus.Web;

public class AdminApiClient(HttpClient httpClient, IHttpContextAccessor accessor)
{
    public async Task<List<StatusCheckViewModel>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await accessor.HttpContext?.GetTokenAsync("access_token");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        List<StatusCheckViewModel> statusCheckSet = new List<StatusCheckViewModel>();

        await foreach (var statusCheck in httpClient.GetFromJsonAsAsyncEnumerable<StatusCheckViewModel>("/admin/statuscheck", cancellationToken))
        {
            if (statusCheck is not null)
            {
                statusCheckSet.Add(statusCheck);
            }
        }
        return statusCheckSet;
    }
}
