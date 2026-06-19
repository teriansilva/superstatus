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

    /// <summary>
    /// #291 Phase B: all webhook targets + per-target usage summary for the
    /// admin Webhooks tab. Operator-only on the API. Returns an empty list on
    /// transport failure so the panel renders a calm empty state instead of
    /// crashing the circuit.
    /// </summary>
    public async Task<List<WebhookViewModel>> GetWebhooksAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<WebhookViewModel>>("/admin/webhooks", cancellationToken) ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>#291 Phase B: create (Id == 0) or update (Id > 0) a webhook
    /// target. Throws with the response body on failure so the dialog can
    /// surface validation errors (422).</summary>
    public async Task<WebhookViewModel> SaveWebhookAsync(WebhookViewModel webhook, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/admin/webhooks", webhook, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to save webhook. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
        return await response.Content.ReadFromJsonAsync<WebhookViewModel>(cancellationToken: cancellationToken) ?? webhook;
    }

    /// <summary>#291 Phase B: enable/disable a webhook target. Returns the new
    /// state, or null if the webhook is unknown (404).</summary>
    public async Task<bool?> SetWebhookEnabledAsync(long webhookId, bool enabled, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/admin/webhooks/{webhookId}/enabled", new { enabled }, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EnabledResponse>(cancellationToken: cancellationToken);
        return body?.Enabled;
    }

    /// <summary>#291 Phase B: delete a webhook target — see
    /// <see cref="DeleteLinkedEntityAsync"/> for the shared 204/404/409 contract.</summary>
    public async Task<LinkedDeleteResult> DeleteWebhookAsync(long webhookId, CancellationToken cancellationToken = default)
        => await DeleteLinkedEntityAsync($"/admin/webhooks/{webhookId}", cancellationToken);

    /// <summary>
    /// #291 Phase C: all alert profiles + per-profile usage summary for the
    /// admin Alerts tab. Operator-only on the API. Returns an empty list on
    /// transport failure so the panel renders a calm empty state instead of
    /// crashing the circuit.
    /// </summary>
    public async Task<List<AlertProfileViewModel>> GetAlertProfilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<AlertProfileViewModel>>("/admin/alert-profiles", cancellationToken) ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>#291 Phase C: create (Id == 0) or update (Id > 0) an alert
    /// profile. Throws with the response body on failure so the dialog can
    /// surface validation errors (422).</summary>
    public async Task<AlertProfileViewModel> SaveAlertProfileAsync(AlertProfileViewModel profile, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/admin/alert-profiles", profile, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to save alert profile. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
        return await response.Content.ReadFromJsonAsync<AlertProfileViewModel>(cancellationToken: cancellationToken) ?? profile;
    }

    /// <summary>#291 Phase C: delete an alert profile — same 204/404/409
    /// delete-guard contract as webhooks (one shared result shape).</summary>
    public async Task<LinkedDeleteResult> DeleteAlertProfileAsync(long profileId, CancellationToken cancellationToken = default)
        => await DeleteLinkedEntityAsync($"/admin/alert-profiles/{profileId}", cancellationToken);

    /// <summary>
    /// #293 Phase C: all SLA targets + per-target usage summary for the admin
    /// SLA panel (SLAs tab). Operator-only on the API. Returns an empty list
    /// on transport failure so the panel renders a calm empty state instead of
    /// crashing the circuit.
    /// </summary>
    public async Task<List<SlaViewModel>> GetSlasAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<SlaViewModel>>("/admin/slas", cancellationToken) ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>#293 Phase C: create (Id == 0) or update (Id > 0) an SLA.
    /// Throws with the response body on failure so the dialog can surface
    /// validation errors (422).</summary>
    public async Task<SlaViewModel> SaveSlaAsync(SlaViewModel sla, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/admin/slas", sla, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to save SLA. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
        return await response.Content.ReadFromJsonAsync<SlaViewModel>(cancellationToken: cancellationToken) ?? sla;
    }

    /// <summary>#293 Phase C: make this SLA the default (transactional switch
    /// server-side). Returns false if the SLA is unknown (404).</summary>
    public async Task<bool> SetDefaultSlaAsync(long slaId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PatchAsync($"/admin/slas/{slaId}/default", content: null, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>#293 Phase C: delete an SLA — the shared 204/404/409 contract
    /// (409 BlockedBy carries the linked check names). The default SLA answers
    /// 422; that message is surfaced by throwing, so the panel's generic error
    /// path shows it.</summary>
    public async Task<LinkedDeleteResult> DeleteSlaAsync(long slaId, CancellationToken cancellationToken = default)
        => await DeleteLinkedEntityAsync($"/admin/slas/{slaId}", cancellationToken);

    /// <summary>
    /// #291: shared delete for linkable entities (webhooks, alert profiles,
    /// SLAs). 204 → deleted; 404 → already gone; 409 → delete blocked while
    /// linked — the result carries the API's LinkedEntitySummary so the UI can
    /// list the linked check names; 422 → rejected outright (e.g. deleting the
    /// default SLA), surfaced as a thrown message.
    /// </summary>
    private async Task<LinkedDeleteResult> DeleteLinkedEntityAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync(path, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new LinkedDeleteResult(false, true, null);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<DeleteConflictBody>(cancellationToken: cancellationToken);
            return new LinkedDeleteResult(false, false, conflict?.Usage ?? new LinkedEntitySummary());
        }
        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            // #293: e.g. "is the default; make another SLA the default first."
            var rejected = await response.Content.ReadFromJsonAsync<DeleteConflictBody>(cancellationToken: cancellationToken);
            throw new HttpRequestException(rejected?.Message ?? "The delete was rejected (422).");
        }
        response.EnsureSuccessStatusCode();
        return new LinkedDeleteResult(true, false, null);
    }

    /// <summary>#291 Phase B: one-off test-fire through the real dispatch
    /// executor. Returns the wire result, or null if the webhook is unknown
    /// (404). Nothing is logged server-side — the caller surfaces this inline.</summary>
    public async Task<WebhookTestFireResult?> TestFireWebhookAsync(long webhookId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"/admin/webhooks/{webhookId}/test", content: null, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WebhookTestFireResult>(cancellationToken: cancellationToken);
    }

    /// <summary>#312: the registered check providers + their config schemas, so the
    /// edit dialog can render the Type selector and the generic config form. Returns an
    /// empty list on failure (the dialog falls back to a single built-in http shape).</summary>
    public async Task<List<ProviderDescriptorViewModel>> GetCheckProvidersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<ProviderDescriptorViewModel>>("/statuscheck/providers", cancellationToken)
                ?? new List<ProviderDescriptorViewModel>();
        }
        catch
        {
            return new List<ProviderDescriptorViewModel>();
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

    /// <summary>#320 Phase 2b: the heartbeat token for an operator's check, so the edit
    /// dialog can render the ping URL. Operator-authenticated (the named client carries the
    /// machine token). Null when the check is unknown or not a heartbeat check (404).</summary>
    public async Task<string?> GetHeartbeatTokenAsync(long statusCheckId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"/statuscheck/{statusCheckId}/heartbeat", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<HeartbeatTokenBody>(cancellationToken: cancellationToken);
        return body?.Token;
    }

    /// <summary>#320 Phase 2b: rotate a heartbeat check's token (operator action). The old
    /// ping URL stops working immediately. Returns the new token, or null on 404.</summary>
    public async Task<string?> RegenerateHeartbeatTokenAsync(long statusCheckId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"/statuscheck/{statusCheckId}/heartbeat/regenerate", content: null, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<HeartbeatTokenBody>(cancellationToken: cancellationToken);
        return body?.Token;
    }

    /// <summary>#320 Phase 2b: forward a public inbound heartbeat ping to the internal API
    /// ping sink. The API is internal-only, so the Web app is the publicly reachable face.
    /// Returns the API's status (204 recorded / 404 unknown token / else) verbatim so the
    /// public endpoint mirrors it.</summary>
    public async Task<System.Net.HttpStatusCode> ForwardHeartbeatAsync(string token, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"/heartbeat/{Uri.EscapeDataString(token)}", content: null, cancellationToken);
        return response.StatusCode;
    }

    private sealed record EnabledResponse(long Id, bool Enabled);
    private sealed record DeleteConflictBody(string? Message, LinkedEntitySummary? Usage);
    private sealed record HeartbeatTokenBody(string Token);
}

/// <summary>#291: outcome of deleting a linkable entity (webhook or alert
/// profile — one shared shape). <c>BlockedBy</c> is the API's 409
/// LinkedEntitySummary payload when the entity is still linked.</summary>
public sealed record LinkedDeleteResult(bool Deleted, bool NotFound, LinkedEntitySummary? BlockedBy);
