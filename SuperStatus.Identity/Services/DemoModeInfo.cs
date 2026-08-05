using SuperStatus.ServiceDefaults;

namespace SuperStatus.Identity.Services;

/// <summary>
/// Issue #377 — singleton view of the <c>PUBLIC_DEMO</c> flag for the Identity
/// service's views and page models, mirroring the shape of SuperStatus.Web's
/// <c>IssuerModeInfo</c>. Resolved once at startup so a page never re-reads the
/// environment, and so tests can inject the off state directly.
/// </summary>
/// <param name="IsEnabled">
/// True on the hosted public demo instance only. Gates the credentials panel, the
/// field prefill, the topbar countdown chip, and (via the initializer) the seeded
/// demo administrator.
/// </param>
public sealed record DemoModeInfo(bool IsEnabled)
{
    public static DemoModeInfo FromEnvironment() => new(DemoMode.IsEnabledFromEnvironment());

    /// <summary>
    /// The instant the demo is next wiped, as an ISO-8601 UTC string for the
    /// <c>data-reset-at</c> attribute that <c>demo-countdown.js</c> reads. Computing it
    /// server-side means the browser never reimplements "when is the next reset" —
    /// the JS only formats a difference. Null when demo mode is off.
    /// </summary>
    public string? NextResetIso8601 =>
        IsEnabled ? DemoMode.NextResetUtc(DateTime.UtcNow).ToString("O") : null;
}
