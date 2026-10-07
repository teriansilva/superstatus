using SuperStatus.Data.Constants;

namespace SuperStatus.Web.Components.StatusCheckOverview;

/// <summary>
/// Pure mapping from a status check's current <see cref="FailType"/> to the
/// HUD frame accent + status-tag vocabulary used by
/// <c>StatusCheckOverviewCard</c> (issue #95 Phase 3c). Extracted so the
/// mapping is unit-testable without rendering the full MudBlazor card.
/// </summary>
public static class ServiceCardState
{
    /// <summary>HudPanel accent: "critical" (red brackets) only for an
    /// actual down state; "" (default accent) otherwise.</summary>
    public static string FrameAccent(FailType? failType) => failType switch
    {
        FailType.StatusCode or FailType.Unreachable => "critical",
        _                                           => "",
    };

    /// <summary>HudLed/HudTag status vocabulary.</summary>
    public static string LedStatus(FailType? failType) => failType switch
    {
        FailType.NoFail       => "up",
        FailType.ResponseTime => "degraded",
        FailType.StatusCode   => "down",
        FailType.Unreachable  => "down",
        _                     => "unknown",
    };

    /// <summary>Uppercase state label shown in the tag.</summary>
    public static string StateLabel(FailType? failType) => failType switch
    {
        FailType.NoFail       => "OPERATIONAL",
        FailType.ResponseTime => "DEGRADED",
        FailType.StatusCode   => "DOWN",
        FailType.Unreachable  => "DOWN",
        _                     => "UNKNOWN",
    };
}
