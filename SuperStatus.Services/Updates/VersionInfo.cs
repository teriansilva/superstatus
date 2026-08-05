using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SuperStatus.Services.Updates;

/// <summary>
/// Issue #249 (epic #248): the running application's version and release channel.
/// <para><see cref="Version"/> is the SemVer string the image was built with — the
/// assembly <see cref="AssemblyInformationalVersionAttribute"/> stamped at
/// <c>docker build</c> time (see the Dockerfiles + release-images.yml). The app
/// reports this rather than introspecting Docker.</para>
/// <para><see cref="Channel"/> is <c>latest</c> for a real release build (a clean
/// SemVer) and <c>edge</c> for main/dev/unstamped builds. Update detection (Phase 2)
/// only offers <c>latest</c>-channel releases.</para>
/// </summary>
public sealed record AppVersionInfo(string Version, string Channel);

/// <summary>
/// Pure, unit-tested helpers for normalizing and classifying the build version
/// string. Kept free of I/O so the parsing rules are testable in isolation.
/// </summary>
public static partial class VersionInfo
{
    /// <summary>Sentinel for a local/source build that wasn't stamped at image build.</summary>
    public const string DevVersion = "0.0.0-dev";
    public const string ChannelLatest = "latest";
    public const string ChannelEdge = "edge";

    // A clean SemVer core with an optional pre-release tag (build metadata is
    // stripped in Normalize before this is applied).
    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVerRegex();

    /// <summary>
    /// Strip a leading <c>v</c> and any build-metadata (<c>+…</c>) suffix and trim.
    /// Blank input collapses to <see cref="DevVersion"/>.
    /// </summary>
    public static string Normalize(string? raw)
    {
        var v = (raw ?? string.Empty).Trim();
        if (v.Length == 0) return DevVersion;

        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];

        if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1]))
            v = v[1..];

        v = v.Trim();
        return v.Length == 0 ? DevVersion : v;
    }

    /// <summary>
    /// A clean release SemVer is the <c>latest</c> channel; anything else —
    /// <c>edge</c>, the dev sentinel, a <c>0.0.0</c> build, or a non-SemVer string —
    /// is <c>edge</c>. (A real pre-release release, e.g. <c>1.2.0-rc.1</c>, stays
    /// <c>latest</c>; the detection logic separately excludes pre-releases from
    /// "newer" offers.)
    /// </summary>
    public static string InferChannel(string? raw)
    {
        var v = Normalize(raw);
        if (v == DevVersion
            || v.StartsWith("0.0.0", StringComparison.Ordinal)
            || v.Contains("edge", StringComparison.OrdinalIgnoreCase)
            || v.Contains("dev", StringComparison.OrdinalIgnoreCase))
        {
            return ChannelEdge;
        }
        return SemVerRegex().IsMatch(v) ? ChannelLatest : ChannelEdge;
    }

    /// <summary>Build the <see cref="AppVersionInfo"/> from a raw build string.</summary>
    public static AppVersionInfo From(string? raw)
    {
        var v = Normalize(raw);
        return new AppVersionInfo(v, InferChannel(v));
    }
}

/// <summary>
/// Reports the running application's version. Registered as a singleton; reads the
/// entry assembly's informational version once at construction.
/// </summary>
public interface IAppVersionProvider
{
    AppVersionInfo Current { get; }
}

/// <inheritdoc />
public sealed class AppVersionProvider : IAppVersionProvider
{
    public AppVersionProvider()
    {
        var raw = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        Current = VersionInfo.From(raw);
    }

    public AppVersionInfo Current { get; }
}

/// <summary>
/// Stable JSON shape for <c>GET /api/version</c> (snake/lower-case contract,
/// additive changes only).
/// </summary>
public sealed record AppVersionResponseDto(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel);
