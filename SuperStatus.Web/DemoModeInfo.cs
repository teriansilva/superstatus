using SuperStatus.ServiceDefaults;

namespace SuperStatus.Web;

/// <summary>
/// Issue #377 — exposes to the Blazor app whether this instance is the hosted public
/// demo (<c>PUBLIC_DEMO=true</c>, set only by <c>docker-compose.demo.yml</c>). Drives
/// the site-wide demo banner and its reset countdown, and nothing else.
///
/// <para>Mirrors <see cref="IssuerModeInfo"/>'s shape, including the explicit-value
/// constructor tests use to render both states without touching the environment.</para>
///
/// <para>Unrelated to the Development-only <c>SUPERSTATUS_DEMO=1</c> fixture harness in
/// <c>SuperStatus.Web/DemoData/</c>, which swaps the API clients for in-memory data.
/// This flag changes no data — the demo instance runs a real API against a real
/// Postgres that simply gets destroyed every hour.</para>
/// </summary>
public sealed class DemoModeInfo
{
    public bool IsEnabled { get; }

    public DemoModeInfo() => IsEnabled = DemoMode.IsEnabledFromEnvironment();

    /// <summary>Explicit-value constructor for tests — bypasses the env probe.</summary>
    public DemoModeInfo(bool isEnabled) => IsEnabled = isEnabled;
}
