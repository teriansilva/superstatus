using SuperStatus.ServiceDefaults;

namespace SuperStatus.Web;

/// <summary>
/// Issue #358 — exposes to the Blazor console whether this instance is running in
/// the self-host <b>dynamic issuer</b> mode (both <c>IDP_PUBLIC_HTTP</c> and
/// <c>WEBAPP_HTTP</c> unset). Drives the "Access &amp; security" allowlist editor
/// and the unpinned-issuer security banner, both of which are only meaningful in
/// dynamic mode — when either <c>WEBAPP_HTTP</c> (reverse-proxy / forwarded-host)
/// or <c>IDP_PUBLIC_HTTP</c> (pinned issuer) is set, the issuer is fixed and
/// neither is shown.
/// </summary>
public sealed class IssuerModeInfo
{
    public bool IsDynamic { get; }

    public IssuerModeInfo()
        => IsDynamic = IdpAuthority.IsDynamicSelfHost(
            Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP"),
            Environment.GetEnvironmentVariable("WEBAPP_HTTP"));

    /// <summary>Explicit-value constructor for tests (and any host that computes the
    /// mode itself) — bypasses the env probe.</summary>
    public IssuerModeInfo(bool isDynamic) => IsDynamic = isDynamic;
}
