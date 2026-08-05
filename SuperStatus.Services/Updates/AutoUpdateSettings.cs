namespace SuperStatus.Services.Updates;

/// <summary>
/// Issue #334: the operator's persisted automatic-update policy — a projection of
/// the <c>AutoUpdate*</c> columns on the <c>SiteSettings</c> singleton.
///
/// The cadence lives here rather than in Watchtower's own scheduler: the updater
/// ships as a pure on-demand executor, so this toggle is the only thing that can
/// start an unattended update. Switching it off in the console really means off.
/// </summary>
/// <param name="Enabled">Whether the daily automatic update runs. Off by default.</param>
/// <param name="TimeUtc">Time of day the update fires, in UTC (v1 is UTC-only).</param>
/// <param name="LastRunUtc">
/// When the automatic update last fired AND was accepted by the updater. Never
/// stamped for a rejected/failed trigger, so a transient token or network error
/// retries on a later cycle instead of suppressing the day's update.
/// </param>
public sealed record AutoUpdateSettingsDto(bool Enabled, TimeOnly TimeUtc, DateTime? LastRunUtc)
{
    /// <summary>What a fresh install (no settings row yet) reports: off, 03:00 UTC, never run.</summary>
    public static readonly AutoUpdateSettingsDto Default = new(Enabled: false, new TimeOnly(3, 0), LastRunUtc: null);
}
