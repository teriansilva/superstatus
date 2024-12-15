using SuperStatus.Data.ViewModels;

namespace SuperStatus.Web;

public class StatusApiClient(HttpClient httpClient)
{
    public async Task<List<StatusCheckViewModel>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        List<StatusCheckViewModel> statusCheckSet = new List<StatusCheckViewModel>();

        await foreach (var statusCheck in httpClient.GetFromJsonAsAsyncEnumerable<StatusCheckViewModel>("/statuscheck", cancellationToken))
        {
            if (statusCheck is not null)
            {
                statusCheckSet.Add(statusCheck);
            }
        }
        return statusCheckSet;
    }

    public async Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusData(long statusCheckId, CancellationToken cancellationToken = default)
    {
        List<HistoricalStatusDataOverviewChartViewModel>? historicalStatusSet = new List<HistoricalStatusDataOverviewChartViewModel>();

        await foreach (var historicalStatus in httpClient.GetFromJsonAsAsyncEnumerable<HistoricalStatusDataOverviewChartViewModel>($"/historicalStatusData/{statusCheckId}", cancellationToken))
        {
            if (historicalStatus is not null)
            {
                historicalStatusSet.Add(historicalStatus);
            }
        }
        return historicalStatusSet;
    }
}
