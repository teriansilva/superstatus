using SuperStatus.Data.ViewModels;

namespace SuperStatus.Web.Components.Hud;

/// <summary>
/// Single source of truth for mapping one day's rollup overview to an
/// <see cref="UptimeStrip"/> cell vocabulary ("gap" / "down" / "degraded" / "up").
///
/// Both the live-status card (<c>StatusCheckHistoricalGraph</c>) and the
/// service-detail page (<c>StatusCheckDetail</c>) feed the same primitive from
/// the same <c>GetHistoricalStatusData</c> source; mapping here keeps the two
/// strips identical and prevents drift (issue #223).
///
/// #200: a day with no samples is "gap" (grey), not "up" — only days that
/// actually ran a check carry a status.
///
/// #293 Phase B: the actual decision rule moved to
/// <see cref="SlaDayClassifier"/> (shared with SuperStatus.Services) and is
/// SLA-driven; this stays the strip-side entry point. The SLA-less overload
/// classifies with the behavior-identical 100/100 targets — the historical
/// worst-of-tick rule.
/// </summary>
public static class UptimeCell
{
    public static string From(HistoricalStatusDataOverviewChartViewModel day)
        => From(day, targetUptimePercent: 100, criticalUptimePercent: 100);

    public static string From(HistoricalStatusDataOverviewChartViewModel day, double targetUptimePercent, double criticalUptimePercent)
        => SlaDayClassifier.Classify(
            day.Total,
            down: day.FailedResponseCount + day.UnreachableCount,
            degraded: day.SlowResponseCount,
            targetUptimePercent, criticalUptimePercent);
}
