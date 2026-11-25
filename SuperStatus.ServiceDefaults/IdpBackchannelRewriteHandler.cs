namespace SuperStatus.ServiceDefaults;

/// <summary>
/// Rewrites the host of outbound OIDC back-channel requests from the public
/// authority URL to an internal one.
///
/// SuperStatus' identity service advertises a single public issuer (e.g.
/// <c>http://id.localhost:8081</c>) so the browser is redirected to a URL it can
/// reach. The web/api services, however, run inside the container network where
/// that public host is not routable (notably <c>*.localhost</c> is pinned to
/// loopback by glibc). They reach identity by its internal service name instead
/// (e.g. <c>http://identity:8080</c>).
///
/// Installing this as the OIDC/JWT <c>BackchannelHttpHandler</c> retargets the
/// TCP connection to the reachable internal host while preserving the public
/// host in the <c>Host</c> header. That matters because OpenIddict builds its
/// discovery endpoint URLs from the request's host: presenting the public host
/// makes the discovery document advertise public endpoints (correct for the
/// browser) and makes issued tokens carry the public issuer, so issuer + RFC
/// 9207 <c>iss</c> validation succeed — even though the bytes travelled over the
/// internal host.
///
/// When the public and internal authorities are identical (the reverse-proxy
/// deployment), no handler is installed — see <see cref="TryCreate"/>.
/// </summary>
public sealed class IdpBackchannelRewriteHandler : DelegatingHandler
{
    private readonly string _publicBase;
    private readonly string _internalBase;
    private readonly string _publicHost;

    public IdpBackchannelRewriteHandler(string publicBase, string internalBase)
        : this(publicBase, internalBase, new HttpClientHandler())
    {
    }

    public IdpBackchannelRewriteHandler(string publicBase, string internalBase, HttpMessageHandler inner)
        : base(inner)
    {
        _publicBase = publicBase.TrimEnd('/');
        _internalBase = internalBase.TrimEnd('/');
        // e.g. "id.localhost:8081" — sent as the Host header so OpenIddict
        // emits public endpoint URLs despite the internal TCP target.
        _publicHost = new Uri(_publicBase, UriKind.Absolute).Authority;
    }

    /// <summary>
    /// Returns a handler when <paramref name="publicBase"/> and
    /// <paramref name="internalBase"/> differ (the self-host / single-host case);
    /// returns <c>null</c> when they are equal or unset (the reverse-proxy case),
    /// so callers can skip installing it entirely.
    /// </summary>
    public static IdpBackchannelRewriteHandler? TryCreate(string? publicBase, string? internalBase)
    {
        if (string.IsNullOrWhiteSpace(publicBase) || string.IsNullOrWhiteSpace(internalBase))
        {
            return null;
        }

        if (string.Equals(publicBase.TrimEnd('/'), internalBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new IdpBackchannelRewriteHandler(publicBase, internalBase);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RewriteUri(request);
        return base.SendAsync(request, cancellationToken);
    }

    private void RewriteUri(HttpRequestMessage request)
    {
        if (request.RequestUri is not { } uri)
        {
            return;
        }

        var url = uri.ToString();
        if (url.StartsWith(_publicBase, StringComparison.OrdinalIgnoreCase))
        {
            request.RequestUri = new Uri(_internalBase + url[_publicBase.Length..]);
            // Keep the public host visible to OpenIddict so it advertises public
            // endpoints / issuer, even though we connect to the internal host.
            request.Headers.Host = _publicHost;
        }
    }
}
