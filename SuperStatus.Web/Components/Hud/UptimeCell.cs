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
/// </summary>
public static class UptimeCell
{
    public static string From(HistoricalStatusDataOverviewChartViewModel day)
        => !day.HasData ? "gap"
            : (day.FailedStatus || day.UnreachableCount > 0) ? "down"
            : day.SlowResponse ? "degraded"
            : "up";
}
