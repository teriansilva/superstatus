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

    /// <summary>#201: lazy per-day detail for an uptime-strip cell. Returns null on a
    /// 404 (unknown check / bad date) so the popover can show a graceful fallback.</summary>
    public async Task<DayDetailViewModel?> GetDayDetailAsync(long statusCheckId, DateOnly date, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<DayDetailViewModel>(
                $"/statuscheck/{statusCheckId}/day/{date:yyyy-MM-dd}", cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
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

    /// <summary>
    /// Issue #226: the 30-day overview for ALL checks in one batched call. The
    /// dashboard fetches this once and hands each card its slice, instead of one
    /// GetHistoricalStatusData call per card (which exhausted the API DB pool with
    /// many checks). Cells are grouped by <c>StatusCheckId</c>.
    /// </summary>
    public async Task<Dictionary<long, List<HistoricalStatusDataOverviewChartViewModel>>> GetHistoricalStatusDataForAllAsync(CancellationToken cancellationToken = default)
    {
        var byCheck = new Dictionary<long, List<HistoricalStatusDataOverviewChartViewModel>>();

        await foreach (var cell in httpClient.GetFromJsonAsAsyncEnumerable<HistoricalStatusDataOverviewChartViewModel>("/statuscheck/historical-overview", cancellationToken))
        {
            if (cell is null) continue;
            if (!byCheck.TryGetValue(cell.StatusCheckId, out var list))
            {
                list = new List<HistoricalStatusDataOverviewChartViewModel>();
                byCheck[cell.StatusCheckId] = list;
            }
            list.Add(cell);
        }
        return byCheck;
    }

    /// <summary>
    /// Most-recent N ticks for one status check (issue #103). Returns null
    /// when the status check is unknown so callers can render their not-found
    /// state instead of crashing the Blazor circuit.
    /// </summary>
    public async Task<List<HistoricalStatusData>?> GetRecentTicksAsync(long statusCheckId, int count, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"/statuscheck/{statusCheckId}/recent?count={count}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<HistoricalStatusData>>(cancellationToken: cancellationToken) ?? new();
    }

    /// <summary>
    /// Aggregated dashboard summary (issue #104) — hero counts, latency
    /// avg/p95, 30-day uptime, incidents, per-service current state +
    /// 30-day strip. One round-trip for the Home hero (#95 Phase 3a).
    /// Returns null on transport failure so the hero can render a calm
    /// "telemetry unavailable" state instead of crashing the circuit.
    /// </summary>
    public async Task<DashboardSummaryViewModel?> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<DashboardSummaryViewModel>("/statuscheck/summary", cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Issue #107 Phase 2: recent webhook execution log for the admin audit
    /// panel. Operator-only on the API (RequireAuthorization). Returns an empty
    /// list on transport failure so the panel renders a calm empty state
    /// instead of crashing the circuit.
    /// </summary>
    public async Task<List<WebhookExecutionLogViewModel>> GetWebhookLogAsync(int count = 100, bool failuresOnly = false, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<WebhookExecutionLogViewModel>>(
                $"/admin/webhook-log?count={count}&failuresOnly={failuresOnly.ToString().ToLowerInvariant()}", cancellationToken)
                ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>
    /// Issue #241/#253: newest-first alert delivery log for the admin AlertLogPanel.
    /// Operator-only on the API. Returns an empty list on transport failure so the
    /// panel renders a calm empty state instead of crashing the circuit.
    /// </summary>
    public async Task<List<AlertDeliveryLogViewModel>> GetAlertLogAsync(int count = 100, bool failuresOnly = false, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<AlertDeliveryLogViewModel>>(
                $"/admin/alert-log?count={count}&failuresOnly={failuresOnly.ToString().ToLowerInvariant()}", cancellationToken)
                ?? new();
        }
        catch (Exception)
        {
            return new();
        }
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

    /// <summary>
    /// Operator action (issue #105): trigger a single manual run.
    /// Returns the resulting tick view-model, or null if the check is
    /// unknown (404). Throws on other failures so the UI can surface them.
    /// </summary>
    public async Task<HistoricalStatusDataViewModel?> RunCheckNowAsync(long statusCheckId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"/statuscheck/{statusCheckId}/run-now", content: null, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HistoricalStatusDataViewModel>(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Operator action (issue #105): pause / resume a check by toggling
    /// Enabled. Returns the new enabled state, or null if the check is
    /// unknown (404).
    /// </summary>
    public async Task<bool?> SetEnabledAsync(long statusCheckId, bool enabled, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/statuscheck/{statusCheckId}/enabled", new { enabled }, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EnabledResponse>(cancellationToken: cancellationToken);
        return body?.Enabled;
    }

    /// <summary>
    /// Operator action (issue #164): permanently delete a check. Returns true
    /// on delete (204), false if the check was already gone (404). Throws on
    /// other failures so the UI can surface them.
    /// </summary>
    public async Task<bool> DeleteStatusCheckAsync(long statusCheckId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"/statuscheck/{statusCheckId}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private sealed record EnabledResponse(long Id, bool Enabled);
}
