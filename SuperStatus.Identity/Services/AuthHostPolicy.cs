using System.Net.Http.Json;
using SuperStatus.Data.ViewModels;
using SuperStatus.ServiceDefaults;

namespace SuperStatus.Identity.Services;

/// <summary>
/// Issue #358 — the self-host dynamic-issuer policy. Answers, per authorization
/// request, whether the browser's host may drive the login flow and whether a
/// <c>redirect_uri</c> is same-origin with it.
///
/// <para><b>Mode.</b> Dynamic mode is active only when <b>both</b>
/// <c>IDP_PUBLIC_HTTP</c> and <c>WEBAPP_HTTP</c> are unset (the no-proxy two-port
/// self-host profile). When either is set — <c>WEBAPP_HTTP</c> for reverse-proxy /
/// forwarded-host, or <c>IDP_PUBLIC_HTTP</c> for a pinned issuer — this policy is
/// never consulted (the OpenIddict handlers that use it are not registered), so
/// cloud / reverse-proxy behavior is untouched.</para>
///
/// <para><b>Allowlist.</b> Read from the API's public, cached <c>GET /settings</c>
/// (<see cref="SiteSettingsViewModel.AllowedAuthHosts"/>). Empty ⇒ relaxed
/// (first-run: accept any request host); non-empty ⇒ hardened (only those hosts).
/// The last successful read is cached; on a read failure the cached value is
/// retained, so a transient API outage can never widen a previously hardened
/// install back to relaxed (Hermes r1/r2). A true cold start with the API
/// unreachable reads as empty ⇒ relaxed, identical to genuine first run.</para>
/// </summary>
public interface IAuthHostPolicy
{
    /// <summary>True on the no-proxy two-port self-host profile (both
    /// <c>IDP_PUBLIC_HTTP</c> and <c>WEBAPP_HTTP</c> unset).</summary>
    bool IsDynamicMode { get; }

    /// <summary>The port the Web app is published on (browser-facing), used to
    /// bound the same-origin <c>redirect_uri</c> check.</summary>
    int WebPort { get; }

    /// <summary>The current effective allowlist (last successful read, retained on
    /// failure). Empty ⇒ relaxed.</summary>
    ValueTask<IReadOnlyList<string>> GetAllowlistAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Caching <see cref="IAuthHostPolicy"/> backed by the API's <c>/settings</c>.
/// Registered as a singleton with a typed <see cref="HttpClient"/> pointed at the
/// internal API authority (<c>API_INTERNAL_HTTP</c>, default <c>http://api:8080</c>).
/// </summary>
public sealed class CachedAuthHostPolicy : IAuthHostPolicy
{
    /// <summary>Named <see cref="HttpClient"/> (base address = internal API) this
    /// singleton resolves per fetch, so it never captures a stale handler.</summary>
    public const string HttpClientName = "authhosts";

    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Last SUCCESSFUL read (null = never). Retained on a later read failure so an
    // outage never reverts hardened → relaxed.
    private IReadOnlyList<string>? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public bool IsDynamicMode { get; }
    public int WebPort { get; }

    /// <param name="ttl">Cache lifetime for a successful read; defaults to 45s.
    /// Tests pass <see cref="TimeSpan.Zero"/> to force a re-read on every call.</param>
    public CachedAuthHostPolicy(IHttpClientFactory httpFactory, TimeSpan? ttl = null)
    {
        _httpFactory = httpFactory;
        _ttl = ttl ?? TimeSpan.FromSeconds(45);
        IsDynamicMode = IdpAuthority.IsDynamicSelfHost(
            Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP"),
            Environment.GetEnvironmentVariable("WEBAPP_HTTP"));
        WebPort = ParsePort(Environment.GetEnvironmentVariable("WEB_PORT"), 8080);
    }

    public async ValueTask<IReadOnlyList<string>> GetAllowlistAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is not null && now - _cachedAt < _ttl)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the gate (another caller may have refreshed).
            now = DateTimeOffset.UtcNow;
            if (_cached is not null && now - _cachedAt < _ttl)
                return _cached;

            try
            {
                var client = _httpFactory.CreateClient(HttpClientName);
                var settings = await client.GetFromJsonAsync<SiteSettingsViewModel>("/settings", cancellationToken);
                // Re-sanitize defensively; the API already normalizes on save.
                var fresh = AuthHostAllowlist.Sanitize(settings?.AllowedAuthHosts);
                _cached = fresh;
                _cachedAt = DateTimeOffset.UtcNow;
                return _cached;
            }
            catch (Exception)
            {
                // Read failure: retain last-known-good (stays hardened if it was).
                // Only a genuine cold start with no prior read falls through to
                // empty ⇒ relaxed, matching true first-run behavior.
                return _cached ?? Array.Empty<string>();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int ParsePort(string? value, int fallback)
        => int.TryParse(value, out var p) && p is > 0 and <= 65535 ? p : fallback;
}
