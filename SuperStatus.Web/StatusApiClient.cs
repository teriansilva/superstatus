using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Web;

public class StatusApiClient(HttpClient httpClient)
{
    public async Task<IPagedResult<StatusCheckViewModel>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        IPagedResult<StatusCheckViewModel> statusCheckSet = new PagedResult<StatusCheckViewModel>();

        return await httpClient.GetFromJsonAsync<PagedResult<StatusCheckViewModel>>("/statuscheck", cancellationToken) ?? statusCheckSet;

    }

    public async Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusData(long statusCheckId, CancellationToken cancellationToken = default)
    {
        List<HistoricalStatusDataOverviewChartViewModel>? historicalStatusSet = new List<HistoricalStatusDataOverviewChartViewModel>();

        await foreach (var historicalStatus in httpClient.GetFromJsonAsAsyncEnumerable<HistoricalStatusDataOverviewChartViewModel>($"/statuscheck/gethistoricaldata/{statusCheckId}", cancellationToken))
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

        using var response = await httpClient.PostAsJsonAsync("/statuscheck/edit", statusCheckToSave, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to save status check. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
    }
}
