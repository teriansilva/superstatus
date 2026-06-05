using System;

namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Per-building snapshot consumed by the Grid renderer (issue #11).
    /// One row per StatusCheck. Computed server-side so the page makes
    /// a single round trip to draw the whole city.
    /// </summary>
    public class GridBuildingViewModel
    {
        public long Id { get; init; }
        public string Title { get; init; } = string.Empty;

        // Stable per-check seed driving deterministic archetype + decoration choices.
        public int Seed { get; init; }

        // Days since the check was created. Drives growth tier.
        public int AgeDays { get; init; }

        // Share of NoFail records over the trailing 30 / 7 day window. Range 0.0–1.0.
        // When no history exists the check is treated as healthy (1.0) so a brand
        // new check renders as pristine rather than wrecked.
        public double Uptime30d { get; init; }
        public double Uptime7d { get; init; }

        // FailType of the most recent recorded check. Matches the enum int values
        // in SuperStatus.Data.Constants.FailType: StatusCode=0, ResponseTime=1,
        // NoFail=2, Unreachable=3. Defaults to NoFail when there is no history.
        public int CurrentFailType { get; init; }

        // Number of trailing non-NoFail records starting from most recent.
        public int ConsecutiveFailures { get; init; }

        public DateTime? LastCheckUtc { get; init; }
    }
}
