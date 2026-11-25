namespace SuperStatus.Services.Updates;

/// <summary>
/// Issue #249 (epic #248): SemVer precedence comparison for update detection.
/// Pure + unit-tested; build metadata is ignored (stripped by <see cref="VersionInfo.Normalize"/>).
/// </summary>
public static partial class VersionInfo
{
    private sealed record SemVer(int Major, int Minor, int Patch, string[] Pre);

    private static SemVer? Parse(string? raw)
    {
        var v = Normalize(raw);
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..] : string.Empty;

        var parts = core.Split('.');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var major) || major < 0) return null;
        if (!int.TryParse(parts[1], out var minor) || minor < 0) return null;
        if (!int.TryParse(parts[2], out var patch) || patch < 0) return null;

        var preIds = pre.Length == 0 ? Array.Empty<string>() : pre.Split('.');
        return new SemVer(major, minor, patch, preIds);
    }

    /// <summary>
    /// SemVer precedence compare: <c>-1</c>/<c>0</c>/<c>1</c>, or <c>null</c> if either
    /// side isn't a parseable <c>X.Y.Z[-pre]</c>. A pre-release has lower precedence
    /// than its release (per the SemVer spec).
    /// </summary>
    public static int? Compare(string? a, string? b)
    {
        var x = Parse(a);
        var y = Parse(b);
        if (x is null || y is null) return null;

        var c = x.Major.CompareTo(y.Major); if (c != 0) return Math.Sign(c);
        c = x.Minor.CompareTo(y.Minor); if (c != 0) return Math.Sign(c);
        c = x.Patch.CompareTo(y.Patch); if (c != 0) return Math.Sign(c);

        bool xPre = x.Pre.Length > 0, yPre = y.Pre.Length > 0;
        if (xPre && !yPre) return -1;   // 1.0.0-rc < 1.0.0
        if (!xPre && yPre) return 1;
        if (!xPre && !yPre) return 0;
        return ComparePre(x.Pre, y.Pre);
    }

    private static int ComparePre(string[] a, string[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var an = int.TryParse(a[i], out var av);
            var bn = int.TryParse(b[i], out var bv);
            int c;
            if (an && bn) c = av.CompareTo(bv);
            else if (an) c = -1;        // numeric identifiers rank below alphanumeric
            else if (bn) c = 1;
            else c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        // A larger set of identifiers wins when all the preceding ones are equal.
        return Math.Sign(a.Length.CompareTo(b.Length));
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a strictly newer release than the
    /// running <paramref name="current"/>. Edge/dev builds are never reported as
    /// "behind" a release; an unparseable pair is not an update (the caller surfaces
    /// that as "couldn't check").
    /// </summary>
    public static bool IsUpdateAvailable(string? current, string? latest)
    {
        if (InferChannel(current) == ChannelEdge) return false;
        return Compare(latest, current) is > 0;
    }
}
