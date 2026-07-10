using System.Text.Json.Serialization;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Services.Services;

namespace SuperStatus.ApiService;

/// <summary>
/// Public, anonymous, machine-readable status endpoint (issue #108).
/// Stable contract — additive changes only inside v1; breaking changes
/// require a new endpoint group (e.g. `/api/status/v2`) and a deprecation
/// header on v1. Documented in docs/api.md.
/// </summary>
public static class PublicStatusApi
{
    public const string ApiVersionHeader = "X-SuperStatus-Api-Version";
    public const string ApiVersion = "1";
    public const string CorsPolicyName = "SuperStatusPublicReadOnly";

    public static void MapPublicStatusApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", async (
            HttpContext http,
            IStatusCheckService statusCheckService,
            IIncidentService incidentService) =>
        {
            // Snapshot the per-service current state. ViewModel set already
            // joins each check with its most-recent historical row.
            var serviceVms = await statusCheckService.GetStatusCheckViewModelSet();
            var openIncidents = await incidentService.GetOpenPublicIncidents();

            var services = serviceVms.Results.Select(vm => new PublicStatusServiceDto(
                Id: vm.Id,
                Title: vm.Title,
                State: MapStateLabel(vm.MostRecentHistoricalStatusCheck?.FailType),
                LastCheckedUtc: vm.MostRecentHistoricalStatusCheck?.TimeOfCheckUTC,
                LastLatencyMs: vm.MostRecentHistoricalStatusCheck is { CheckFailed: false } r ? r.ResponseTimeInMs : (long?)null,
                ExpectedStatusCode: vm.ExpectedStatusCode,
                // #293 Phase C: the public contract keeps its field name; the
                // value is the linked SLA's slow threshold.
                ExpectedResponseTimeMs: vm.EffectiveSlowThresholdMs
            )).ToList();

            var incidentDtos = openIncidents.Select(i => new PublicStatusOpenIncidentDto(
                Id: i.Id,
                Title: i.Title,
                StartedUtc: i.Created,
                Severity: i.Severity.ToString().ToLowerInvariant()   // #106 PR2: minor/severe/critical
            )).ToList();

            string overall = ComputeOverall(services, incidentDtos);

            var payload = new PublicStatusResponseDto(
                Overall: overall,
                GeneratedUtc: DateTime.UtcNow,
                Services: services,
                IncidentsOpen: incidentDtos
            );

            // Anonymous endpoint, always fresh, versioned contract.
            http.Response.Headers["Cache-Control"] = "no-store, max-age=0";
            http.Response.Headers[ApiVersionHeader] = ApiVersion;
            return Results.Json(payload, statusCode: 200, contentType: "application/json; charset=utf-8");
        })
        .WithName("GetPublicStatus")
        .RequireCors(CorsPolicyName);
    }

    /// <summary>
    /// FailType → public state vocabulary. Defined in one place + unit-tested
    /// so the UI summary and the public API never disagree.
    /// </summary>
    public static string MapStateLabel(FailType? f) => f switch
    {
        FailType.NoFail        => "up",
        FailType.ResponseTime  => "degraded",
        FailType.StatusCode    => "down",
        FailType.Unreachable   => "down",
        null                   => "unknown",
        _                      => "unknown",
    };

    /// <summary>
    /// Overall operational state rule:
    ///   up        — every service up AND no public open incidents.
    ///   degraded  — at least one service degraded, OR at least one public open incident.
    ///   down      — at least one service down.
    /// Mirrors the rule documented in docs/api.md.
    /// </summary>
    public static string ComputeOverall(IReadOnlyCollection<PublicStatusServiceDto> services, IReadOnlyCollection<PublicStatusOpenIncidentDto> openIncidents)
    {
        if (services.Any(s => s.State == "down")) return "down";
        if (services.Any(s => s.State == "degraded")) return "degraded";
        if (openIncidents.Count > 0) return "degraded";
        return "up";
    }

}

/// <summary>
/// v1 response shape. **Adding fields is allowed within v1.** Renaming or
/// removing fields is a breaking change and must go to /api/status/v2.
/// </summary>
public sealed record PublicStatusResponseDto(
    [property: JsonPropertyName("overall")]         string Overall,
    [property: JsonPropertyName("generated_utc")]   DateTime GeneratedUtc,
    [property: JsonPropertyName("services")]        IReadOnlyList<PublicStatusServiceDto> Services,
    [property: JsonPropertyName("incidents_open")]  IReadOnlyList<PublicStatusOpenIncidentDto> IncidentsOpen
);

public sealed record PublicStatusServiceDto(
    [property: JsonPropertyName("id")]                          long Id,
    [property: JsonPropertyName("title")]                       string Title,
    [property: JsonPropertyName("state")]                       string State,
    [property: JsonPropertyName("last_checked_utc")]            DateTime? LastCheckedUtc,
    [property: JsonPropertyName("last_latency_ms")]             long? LastLatencyMs,
    [property: JsonPropertyName("expected_status_code")]        int ExpectedStatusCode,
    [property: JsonPropertyName("expected_response_time_ms")]   long ExpectedResponseTimeMs
);

public sealed record PublicStatusOpenIncidentDto(
    [property: JsonPropertyName("id")]           long Id,
    [property: JsonPropertyName("title")]        string Title,
    [property: JsonPropertyName("started_utc")]  DateTime StartedUtc,
    [property: JsonPropertyName("severity")]     string Severity
);
