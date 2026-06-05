using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Web.DemoData;

/// <summary>
/// Issue #126/#139: seeded sample data for the Development-only "demo mode"
/// (see <see cref="DemoApiHandler"/>). Lets the real public pages render with a
/// realistic fleet — no API/Identity/Postgres — so the visual harness can
/// screenshot the actual UI for design review. NEVER wired outside Development.
///
/// Mirrors the design-mock fleet: 15 services, 14 up + 1 degraded, ~99.94%
/// uptime, 3 incidents in the 30-day window.
/// </summary>
public static class DemoData
{
    private record Svc(long Id, string Title, string Url, long LatencyMs, FailType State);

    // A generic example fleet for the demo (id, title, url, latency, state).
    // Deliberately uses reserved example.com hostnames so the showcase leaks no
    // real infrastructure — swap in your own services when you self-host.
    private static readonly Svc[] Fleet =
    [
        new(1,  "Public API",            "https://api.example.com/health",      82,   FailType.NoFail),
        new(2,  "Marketing site",        "https://www.example.com/",            214,  FailType.NoFail),
        new(3,  "Payments gateway",      "https://payments.example.com/v1/health", 1420, FailType.ResponseTime),
        new(4,  "Git server",            "https://git.example.com/",            96,   FailType.NoFail),
        new(5,  "Mail relay — SMTP",     "tcp://mail.example.com:25",           38,   FailType.NoFail),
        new(6,  "File storage",          "https://files.example.com/health",    181,  FailType.NoFail),
        new(7,  "Metrics dashboard",     "https://metrics.example.com/",        120,  FailType.NoFail),
        new(8,  "Prometheus metrics",    "https://prom.example.com/-/healthy",  64,   FailType.NoFail),
        new(9,  "Auth gateway",          "https://auth.example.com/health",     210,  FailType.NoFail),
        new(10, "Search service",        "https://search.example.com/",         158,  FailType.NoFail),
        new(11, "Object storage",        "https://storage.example.com/",        72,   FailType.NoFail),
        new(12, "CDN edge",              "https://cdn.example.com/",            133,  FailType.NoFail),
        new(13, "Identity provider",     "https://id.example.com/health",       88,   FailType.NoFail),
        new(14, "Background jobs",       "https://jobs.example.com/health",     176,  FailType.NoFail),
        new(15, "WebSocket gateway",     "https://ws.example.com/",             109,  FailType.NoFail),
    ];

    private static string LedState(FailType t) => t switch
    {
        FailType.NoFail => "up",
        FailType.ResponseTime => "degraded",
        _ => "down",
    };

    // A mostly-healthy 30-cell strip; the degraded service gets a few amber/red cells.
    private static IReadOnlyList<string> Strip(FailType t)
    {
        var s = new string[30];
        for (int i = 0; i < 30; i++) s[i] = "up";
        if (t == FailType.ResponseTime) { s[19] = "degraded"; s[20] = "degraded"; s[27] = "down"; }
        else if (t == FailType.NoFail && false) { }
        return s;
    }

    /// <summary>Issue #167 Phase 2: demo branding so the gallery shell renders
    /// a custom title + a non-default accent — making the recolor visible in the
    /// screenshots. Logo left empty (a network/data image would only add a
    /// broken-image artifact to the shots).</summary>
    public static SiteSettingsViewModel Settings() => new()
    {
        Title = "Acme Service Status",
        Subtitle = "Acme operational status information",
        LogoUrl = string.Empty,
        AccentColor = "#f5a524", // tactical amber — deliberately not the #3fbf6f default
        // #181: demo instance is already set up → gallery surfaces render the
        // real console, not the first-run wizard.
        OnboardedUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        // #170: a customized static footer + links so the shots show the feature.
        FooterText = "© 2026 Acme — operational status",
        FooterLinks = new()
        {
            new FooterLink { Label = "Privacy", Url = "https://www.example.com/privacy" },
            new FooterLink { Label = "Status API", Url = "https://status.example.com/statuscheck" },
        },
        ShowAdminLink = true,
    };

    public static DashboardSummaryViewModel Summary()
    {
        int up = Fleet.Count(f => f.State == FailType.NoFail);
        int degraded = Fleet.Count(f => f.State == FailType.ResponseTime);
        int down = Fleet.Count(f => f.State is FailType.StatusCode or FailType.Unreachable);
        var per = Fleet.Select(f => new DashboardPerServiceViewModel(f.Id, f.Title, LedState(f.State), Strip(f.State))).ToList();
        return new DashboardSummaryViewModel(
            Services: new DashboardServiceCountsViewModel(up, degraded, down, Fleet.Length),
            LatencyMs: new DashboardLatencyViewModel(142, 387),
            Uptime30dPct: 99.94,
            Incidents30d: 3,
            PerService: per,
            Overall: degraded > 0 ? "degraded" : "up",
            GeneratedUtc: new DateTime(2026, 5, 30, 14, 22, 0, DateTimeKind.Utc));
    }

    public static PagedResult<StatusCheckViewModel> Checks()
    {
        var list = Fleet.Select(f => new StatusCheckViewModel
        {
            Id = f.Id,
            Title = f.Title,
            StatusCheckUrl = f.Url,
            Description = string.Empty,
            ServiceLogoUrl = string.Empty,
            Enabled = true,
            ExpectedStatusCode = 200,
            ExpectedResponseTimeInMs = 1000,
            IntervalSeconds = 30,
            ConsecutiveFailures = f.State == FailType.NoFail ? 0 : 2,
            MostRecentHistoricalStatusCheck = new HistoricalStatusDataViewModel
            {
                Id = f.Id,
                HttpStatusCode = 200,
                ResponseTimeInMs = f.LatencyMs,
                TimeOfCheckUTC = new DateTime(2026, 5, 30, 14, 21, 50, DateTimeKind.Utc),
                CheckFailed = f.State is FailType.Unreachable,
                FailType = f.State,
            },
        }).ToList();
        return new PagedResult<StatusCheckViewModel>
        {
            Results = list, RowCount = list.Count, PageSize = list.Count, CurrentPage = 1, PageCount = 1,
        };
    }

    public static List<GridBuildingViewModel> Grid()
    {
        var now = new DateTime(2026, 5, 30, 14, 22, 0, DateTimeKind.Utc);
        return Fleet.Select(f => new GridBuildingViewModel
        {
            Id = f.Id, Title = f.Title, Seed = (int)(f.Id * 7919 % 1000), AgeDays = 40 + (int)f.Id,
            Uptime30d = f.State == FailType.NoFail ? 1.0 : 0.94,
            Uptime7d = f.State == FailType.NoFail ? 1.0 : 0.97,
            CurrentFailType = (int)f.State,
            ConsecutiveFailures = f.State == FailType.NoFail ? 0 : 2,
            LastCheckUtc = now.AddSeconds(-10),
        }).ToList();
    }

    /// <summary>True for a seeded service id (the detail page 404s otherwise).</summary>
    public static bool IsKnownId(long id) => Fleet.Any(f => f.Id == id);

    /// <summary>Recent raw ticks for the service-detail page (GET /statuscheck/{id}/recent).</summary>
    public static List<HistoricalStatusData> RecentTicks(long id, int count)
    {
        var svc = Fleet.FirstOrDefault(f => f.Id == id) ?? Fleet[0];
        var now = new DateTime(2026, 5, 30, 14, 21, 50, DateTimeKind.Utc);
        int n = Math.Clamp(count, 1, 50);
        var list = new List<HistoricalStatusData>(n);
        for (int i = 0; i < n; i++)
        {
            // The degraded service shows a couple of slow ticks; others clean.
            bool slow = svc.State == FailType.ResponseTime && (i == 0 || i == 3);
            list.Add(new HistoricalStatusData
            {
                Id = id * 100 + i,
                StatusCheckId = id,
                HttpStatusCode = 200,
                ResponseTimeInMs = slow ? 1420 : svc.LatencyMs,
                TimeOfCheckUTC = now.AddSeconds(-i * 30),
                CheckFailed = false,
                FailType = slow ? FailType.ResponseTime : FailType.NoFail,
            });
        }
        return list;
    }

    public static List<HistoricalStatusDataOverviewChartViewModel> Historical(long id)
    {
        var today = new DateOnly(2026, 5, 30);
        var list = new List<HistoricalStatusDataOverviewChartViewModel>(30);
        for (int i = 29; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            // mostly clean; the degraded service (id 3) shows a couple of slow days.
            // #200: every demo day has samples (Total > 0) so the demo strip stays
            // fully populated — no grey "gap" cells in the showcase.
            int slow = (id == 3 && (i == 10 || i == 2)) ? 4 : 0;
            list.Add(new HistoricalStatusDataOverviewChartViewModel(id, date, 0, slow, 0, total: 144));
        }
        return list;
    }

    public static IDictionary<DateTime, List<IncidentViewModel>> Incidents()
    {
        var today = new DateTime(2026, 5, 28, 0, 0, 0, DateTimeKind.Utc);
        var older = new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc);
        return new Dictionary<DateTime, List<IncidentViewModel>>
        {
            [today] =
            [
                new IncidentViewModel
                {
                    Id = 1, Title = "Payments gateway — elevated latency",
                    Description = "P95 latency above the 1s window for two consecutive ticks. Upstream provider reporting slow response times. Mitigation: routing new requests to the fallback region.",
                    Severity = IncidentSeverity.Severe, VisibleToPublic = true, Resolved = false,
                    Created = today.AddHours(14), ResolvedUtc = null,
                },
            ],
            [older] =
            [
                new IncidentViewModel
                {
                    Id = 2, Title = "CI runners offline (12 min)",
                    Description = "Self-hosted runner pool exhausted by a stuck background job after the database connection pool depleted. Fixed in a follow-up change; no customer impact.",
                    Severity = IncidentSeverity.Minor, VisibleToPublic = true, Resolved = true,
                    Created = older.AddHours(9), ResolvedUtc = older.AddHours(9).AddMinutes(12),
                },
                new IncidentViewModel
                {
                    Id = 3, Title = "Mail relay — brief delivery delay",
                    Description = "Greylisting backlog after a burst; cleared within the retry window.",
                    Severity = IncidentSeverity.Minor, VisibleToPublic = true, Resolved = true,
                    Created = older.AddHours(3), ResolvedUtc = older.AddHours(4),
                },
            ],
        };
    }
}
