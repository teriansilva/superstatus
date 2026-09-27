using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SuperStatus.ServiceDefaults;

/// <summary>
/// Issue #358 — normalization + matching for the operator-configured "allowed
/// sign-in hosts" allowlist that hardens the self-host dynamic-issuer mode.
///
/// Shared by every place the same host-matching contract must hold:
/// <list type="bullet">
///   <item><b>SiteSettingsService</b> — sanitizes/normalizes entries on save.</item>
///   <item><b>Identity</b> — the OpenIddict host gate + same-origin redirect check.</item>
///   <item><b>Web</b> — dynamic-mode issuer validation.</item>
/// </list>
///
/// An entry is a bare host (<c>status.example.com</c>, <c>20.106.154.24</c>,
/// <c>[::1]</c>) or a <c>host:port</c> (<c>20.106.154.24:8081</c>). Matching is
/// host-first: an entry <i>without</i> a port matches that host on ANY port; an
/// entry <i>with</i> a port matches only that host+port. Hosts are compared
/// case-insensitively, IDN-folded to punycode, with a trailing dot stripped and
/// IP literals canonicalized (IPv6 compared unbracketed).
///
/// An empty allowlist means "not configured" — <see cref="Allows"/> returns
/// false for every host, and callers decide whether that means relaxed
/// (accept-any, first-run) or hardened (deny). This type only answers the
/// narrow question "is this host explicitly on the list".
/// </summary>
public static class AuthHostAllowlist
{
    /// <summary>Bound the stored allowlist so a runaway payload can't bloat the row.</summary>
    public const int MaxEntries = 16;

    // Case-insensitive is redundant (we lower-case first) but harmless; the pattern
    // accepts single-label hosts ("localhost") and multi-label FQDNs, ≤253 chars,
    // labels of [a-z0-9-] not starting/ending with a hyphen.
    private static readonly Regex Hostname = new(
        @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$",
        RegexOptions.Compiled);

    /// <summary>A normalized entry: a canonical lower-case host (IPv6 unbracketed)
    /// and an optional port. <c>Port == null</c> ⇒ match the host on any port.</summary>
    public readonly record struct HostEntry(string Host, int? Port);

    /// <summary>
    /// Normalize a bare host label to its canonical comparison form, or null when
    /// it isn't a usable IP literal / hostname. Lower-cases, strips a trailing dot,
    /// canonicalizes IPv4/IPv6 literals, and folds IDN hostnames to punycode ASCII.
    /// IPv6 is returned unbracketed (e.g. <c>::1</c>).
    /// </summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return null;

        // A request host can arrive bracketed for IPv6 (e.g. "[::1]"); IPAddress
        // doesn't accept brackets, so strip a matched pair before parsing.
        if (h.Length > 1 && h[0] == '[' && h[^1] == ']')
            h = h[1..^1];

        // IPv6 literal (contains a colon and no hostname could) → canonical compressed form.
        if (h.Contains(':'))
            return IPAddress.TryParse(h, out var ip6) ? ip6.ToString() : null;

        // IPv4 literal → canonical dotted-quad.
        if (IPAddress.TryParse(h, out var ip4)) return ip4.ToString();

        // Hostname → punycode ASCII, then structural sanity-check.
        try
        {
            var ascii = new IdnMapping().GetAscii(h);
            return Hostname.IsMatch(ascii) ? ascii : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse + normalize a single raw entry into a <see cref="HostEntry"/>, or null
    /// when it isn't a usable host / host:port. Tolerant of a pasted scheme and/or
    /// path (<c>https://status.example.com:8081/x</c> → host <c>status.example.com</c>,
    /// port 8081) — only the authority matters. A port outside 1–65535 is rejected.
    /// </summary>
    public static HostEntry? TryParseEntry(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // Drop a scheme and anything from the first path/query/fragment onward.
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var cut = s.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) s = s[..cut];
        if (s.Length == 0) return null;

        string hostPart;
        string? portPart = null;

        if (s.StartsWith('['))
        {
            // [ipv6] or [ipv6]:port
            var close = s.IndexOf(']');
            if (close < 0) return null;
            hostPart = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.StartsWith(':')) portPart = rest[1..];
            else if (rest.Length > 0) return null;
        }
        else
        {
            var first = s.IndexOf(':');
            var last = s.LastIndexOf(':');
            if (first >= 0 && first == last)
            {
                // exactly one colon → host:port
                hostPart = s[..first];
                portPart = s[(first + 1)..];
            }
            else
            {
                // zero colons → bare host; many colons → bare IPv6 literal (no port)
                hostPart = s;
            }
        }

        var host = NormalizeHost(hostPart);
        if (host is null) return null;

        int? port = null;
        if (portPart is not null)
        {
            if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out var p)
                || p < 1 || p > 65535)
                return null;
            port = p;
        }

        return new HostEntry(host, port);
    }

    /// <summary>The canonical stored/display string for a raw entry
    /// (<c>host</c> or <c>host:port</c>; IPv6 bracketed), or null if invalid.</summary>
    public static string? Canonical(string? raw)
    {
        if (TryParseEntry(raw) is not { } e) return null;
        var host = e.Host.Contains(':') ? $"[{e.Host}]" : e.Host;
        return e.Port is { } p ? $"{host}:{p}" : host;
    }

    /// <summary>
    /// Normalize a list of raw entries for storage: canonicalize each, drop the
    /// unparseable, de-duplicate (case-insensitively, order-preserving), and cap at
    /// <see cref="MaxEntries"/>.
    /// </summary>
    public static List<string> Sanitize(IEnumerable<string>? entries)
    {
        var result = new List<string>();
        if (entries is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries)
        {
            if (Canonical(raw) is not { } c) continue;
            if (!seen.Add(c)) continue;
            result.Add(c);
            if (result.Count >= MaxEntries) break;
        }
        return result;
    }

    /// <summary>
    /// True when <paramref name="requestHost"/> (with optional
    /// <paramref name="requestPort"/>) is explicitly permitted by the allowlist.
    /// An allowlist entry without a port matches any port on that host; an entry
    /// with a port matches only that host+port. An empty allowlist ⇒ false.
    /// </summary>
    public static bool Allows(IEnumerable<string>? allowlist, string? requestHost, int? requestPort)
    {
        if (allowlist is null) return false;
        var reqHost = NormalizeHost(requestHost);
        if (reqHost is null) return false;

        foreach (var raw in allowlist)
        {
            if (TryParseEntry(raw) is not { } entry) continue;
            if (!string.Equals(entry.Host, reqHost, StringComparison.Ordinal)) continue;
            if (entry.Port is null || entry.Port == requestPort) return true;
        }
        return false;
    }

    /// <summary>
    /// True when a front-channel callback URI is <b>same-origin</b> with the login
    /// request: same <paramref name="expectedScheme"/>, same (normalized) host, the
    /// given <paramref name="expectedPort"/>, and the given <paramref name="expectedPath"/>.
    /// This is the open-redirector guard for dynamic mode — the sign-in / sign-out
    /// callback must return to the exact origin the user is on. Scheme is compared
    /// (not just host+port+path) so a same-host wrong-scheme callback can't slip past
    /// under HTTPS forwarding. Returns false for a null/relative/malformed URI.
    /// </summary>
    public static bool IsSameOriginCallback(string? callbackUri, string? requestHost, int expectedPort, string expectedPath, string expectedScheme)
    {
        if (string.IsNullOrEmpty(callbackUri)
            || !Uri.TryCreate(callbackUri, UriKind.Absolute, out var uri))
            return false;

        var reqHost = NormalizeHost(requestHost);
        var uriHost = NormalizeHost(uri.Host);
        return reqHost is not null && uriHost is not null
            && string.Equals(uri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(reqHost, uriHost, StringComparison.Ordinal)
            && uri.Port == expectedPort
            && string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strictly normalize a full submitted allowlist for a write. Blank entries are
    /// ignored (not errors); a non-blank entry that isn't a valid host / host:port,
    /// or a normalized count over <see cref="MaxEntries"/>, is a hard failure —
    /// unlike <see cref="Sanitize"/> (which silently drops), this lets the API reject
    /// a bad payload with 4xx and leave the stored row unchanged, so a hardened
    /// allowlist can't be accidentally cleared to <c>[]</c> by typos. An input of all
    /// blanks / empty normalizes to an empty list (the intentional "clear" operation).
    /// </summary>
    public static bool TryNormalizeForWrite(IEnumerable<string>? entries, out List<string> normalized, out string? error)
    {
        normalized = new();
        error = null;
        if (entries is null) return true;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue; // blanks ignored, not rejected
            if (Canonical(raw) is not { } c)
            {
                error = $"'{raw}' is not a valid host or host:port.";
                normalized = new();
                return false;
            }
            if (seen.Add(c)) normalized.Add(c);
        }

        if (normalized.Count > MaxEntries)
        {
            error = $"Too many allowed hosts — at most {MaxEntries}.";
            normalized = new();
            return false;
        }
        return true;
    }
}
