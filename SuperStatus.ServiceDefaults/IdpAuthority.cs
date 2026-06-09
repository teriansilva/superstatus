namespace SuperStatus.ServiceDefaults;

/// <summary>
/// Helpers for reasoning about the two OIDC authority URLs a SuperStatus service
/// is configured with:
///
/// <list type="bullet">
///   <item><c>IDP_PUBLIC_HTTP</c> — the browser-facing authority (the issuer the
///   browser is redirected to).</item>
///   <item><c>IDP_HTTP</c> — the back-channel authority the service uses to reach
///   Identity over the container network.</item>
/// </list>
///
/// Three valid shapes:
/// <list type="number">
///   <item><b>Reverse-proxy deployment</b> — both equal a public HTTPS URL.
///   No back-channel rewrite, secure cookies.</item>
///   <item><b>Single-host localhost trial</b> — public is a plain-HTTP
///   <c>*.localhost</c> URL, internal is the compose service name. Back-channel
///   rewrite + relaxed cookies.</item>
///   <item><b>Reverse-proxy in front of the self-host compose</b> — public is a
///   public HTTPS URL, internal is the compose service name. Back-channel rewrite
///   but secure cookies (the browser is on HTTPS).</item>
/// </list>
///
/// The invalid shape this guards against: a loopback/localhost public issuer
/// paired with an HTTPS internal authority — the classic "operator set IDP_HTTP
/// to their real domain but left IDP_PUBLIC_HTTP at the localhost default", which
/// would silently redirect users to <c>id.localhost</c>.
/// </summary>
public static class IdpAuthority
{
    /// <summary>True when the browser-facing authority is plain HTTP, i.e. the
    /// trial stack where Secure / <c>SameSite=None</c> cookies can't be returned
    /// and the cookie/response-mode relaxations are required.</summary>
    public static bool BrowserUsesPlainHttp(string? publicAuthority) =>
        !string.IsNullOrWhiteSpace(publicAuthority)
        && publicAuthority.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Throws when the public/back-channel authorities form a misconfiguration:
    /// a loopback/localhost public issuer with an HTTPS back-channel authority.
    /// No-op for the three valid shapes and when either value is unset.
    /// </summary>
    public static void Validate(string? publicAuthority, string? internalAuthority)
    {
        if (string.IsNullOrWhiteSpace(publicAuthority) || string.IsNullOrWhiteSpace(internalAuthority))
        {
            return;
        }

        if (!Uri.TryCreate(publicAuthority, UriKind.Absolute, out var pub)
            || !Uri.TryCreate(internalAuthority, UriKind.Absolute, out var intl))
        {
            return;
        }

        // Equal authorities are always valid (reverse-proxy deployment, or the
        // Aspire dev path where only IDP_HTTP is set and the public value falls
        // back to it — both an https://localhost:<port> URL). The footgun is only
        // a loopback public issuer paired with a *different* HTTPS back-channel.
        var equal = string.Equals(
            publicAuthority.TrimEnd('/'), internalAuthority.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

        if (!equal && IsLoopback(pub) && intl.Scheme == Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"IDP_PUBLIC_HTTP ('{publicAuthority}') is a localhost/loopback authority but " +
                $"IDP_HTTP ('{internalAuthority}') is HTTPS. For a TLS/reverse-proxy deployment, set " +
                "IDP_PUBLIC_HTTP to your public Identity URL (the same value as IDP_HTTP). " +
                "Leaving it unset keeps the localhost default. See docs/self-hosting.md.");
        }
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
