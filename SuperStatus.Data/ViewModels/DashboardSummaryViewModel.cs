using System.Text.Json.Serialization;

namespace SuperStatus.Data.ViewModels;

/// <summary>
/// Public dashboard summary aggregation (issue #104). Returned by
/// <c>GET /statuscheck/summary</c> and consumed by the Home hero panel.
/// One round-trip replaces the N-per-card poll pattern.
///
/// State vocabulary mirrors the public API contract documented in
/// <c>docs/api.md</c>: <c>"up" | "degraded" | "down" | "unknown" | "gap"</c>.
/// </summary>
public sealed record DashboardSummaryViewModel(
    [property: JsonPropertyName("services")]        DashboardServiceCountsViewModel Services,
    [property: JsonPropertyName("latency_ms")]      DashboardLatencyViewModel LatencyMs,
    [property: JsonPropertyName("uptime_30d_pct")]  double Uptime30dPct,
    [property: JsonPropertyName("incidents_30d")]   int Incidents30d,
    [property: JsonPropertyName("per_service")]     IReadOnlyList<DashboardPerServiceViewModel> PerService,
    [property: JsonPropertyName("overall")]         string Overall,
    [property: JsonPropertyName("generated_utc")]   DateTime GeneratedUtc
);

public sealed record DashboardServiceCountsViewModel(
    [property: JsonPropertyName("up")]         int Up,
    [property: JsonPropertyName("degraded")]   int Degraded,
    [property: JsonPropertyName("down")]       int Down,
    [property: JsonPropertyName("total")]      int Total
);

public sealed record DashboardLatencyViewModel(
    [property: JsonPropertyName("avg")]    int? Avg,
    [property: JsonPropertyName("p95")]    int? P95
);

public sealed record DashboardPerServiceViewModel(
    [property: JsonPropertyName("status_check_id")]  long StatusCheckId,
    [property: JsonPropertyName("title")]            string Title,
    [property: JsonPropertyName("current_state")]    string CurrentState,
    [property: JsonPropertyName("uptime_30d")]       IReadOnlyList<string> Uptime30d
);
