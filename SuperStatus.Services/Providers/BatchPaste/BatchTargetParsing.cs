namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #342: the single server-side place a raw pasted line becomes a provider's
    /// canonical target value (mirrors how <c>ProviderConfigWriter.Build</c> is the single
    /// place <c>ConfigJson</c> is written). Shared by every URL-target provider (http →
    /// <c>url</c>, ai → <c>baseUrl</c>) via <see cref="ICheckProvider.TryParseBatchTarget"/>.
    /// Canonicalisation (add scheme, drop a lone trailing slash) is what makes dedup compare
    /// meaningfully — <c>https://host/</c> and <c>https://host</c> collapse to one target.
    /// </summary>
    public static class BatchTargetParsing
    {
        /// <summary>
        /// Parse + canonicalise one pasted line as an http(s) URL. A line with no scheme
        /// defaults to <c>https://</c>. Returns false with a human reason for anything that
        /// isn't a valid http(s) URL with a host.
        /// </summary>
        public static bool TryParseUrlTarget(string line, out string canonical, out string? error)
        {
            canonical = string.Empty;
            error = null;

            var s = (line ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                error = "empty line";
                return false;
            }

            // Tolerate a bare host (no scheme) — the common paste shape — as https.
            if (!s.Contains("://", StringComparison.Ordinal))
                s = "https://" + s;

            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host))
            {
                error = "not a valid http(s) URL";
                return false;
            }

            // Canonical form: scheme://authority[/path][?query], with a lone trailing
            // slash on the path removed so "…/" and "…" dedup as the same target.
            // (Uri.Authority lowercases the host and omits a default port.)
            var path = uri.AbsolutePath;
            path = path == "/" ? string.Empty : path.TrimEnd('/');
            canonical = $"{uri.Scheme}://{uri.Authority}{path}{uri.Query}";
            return true;
        }

        /// <summary>Host of a canonical URL target (for the derived check title). Falls
        /// back to the whole value if it somehow isn't a parseable absolute URL.</summary>
        public static string DeriveHost(string canonicalTarget)
            => Uri.TryCreate(canonicalTarget, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host
                : canonicalTarget;

        /// <summary>Path of a canonical URL target (empty for a bare host), used by the
        /// <c>{host}{path}</c> naming template.</summary>
        public static string DerivePath(string canonicalTarget)
        {
            if (Uri.TryCreate(canonicalTarget, UriKind.Absolute, out var uri))
            {
                var p = uri.AbsolutePath;
                return p == "/" ? string.Empty : p;
            }
            return string.Empty;
        }
    }
}
