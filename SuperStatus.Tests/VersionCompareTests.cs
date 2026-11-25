using SuperStatus.Services.Updates;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #249 (epic #248): SemVer precedence comparison + the "is an update
/// available" rule behind update detection.
/// </summary>
[TestClass]
public class VersionCompareTests
{
    [TestMethod]
    [DataRow("1.0.1", "1.0.0", 1)]
    [DataRow("1.0.0", "1.0.1", -1)]
    [DataRow("1.0.0", "1.0.0", 0)]
    [DataRow("v1.2.0", "1.1.9", 1)]      // leading v + minor bump
    [DataRow("2.0.0", "1.9.9", 1)]
    [DataRow("1.0.0", "1.0.0-rc.1", 1)]  // release > its pre-release
    [DataRow("1.0.0-rc.1", "1.0.0", -1)]
    [DataRow("1.0.0-rc.2", "1.0.0-rc.1", 1)]
    [DataRow("1.0.0-rc.1", "1.0.0-rc.1", 0)]
    public void Compare_followsSemVerPrecedence(string a, string b, int expected)
        => Assert.AreEqual(expected, VersionInfo.Compare(a, b));

    [TestMethod]
    [DataRow("edge", "1.0.0")]
    [DataRow("1.0.0", "not-a-version")]
    [DataRow("1.0", "1.0.0")]            // not a full core
    public void Compare_unparseable_returnsNull(string a, string b)
        => Assert.IsNull(VersionInfo.Compare(a, b));

    [TestMethod]
    [DataRow("1.0.0", "1.0.1", true)]        // newer release available
    [DataRow("1.0.1", "1.0.1", false)]       // equal
    [DataRow("1.0.2", "1.0.1", false)]       // running ahead of latest
    [DataRow("1.2.0-rc.1", "1.2.0", true)]   // stable supersedes the rc
    [DataRow("0.0.0-dev", "1.0.0", false)]   // edge/dev builds are never "behind"
    [DataRow("edge", "1.0.0", false)]
    [DataRow("1.0.0", "garbage", false)]     // unparseable latest → not an update
    public void IsUpdateAvailable_offersOnlyNewerReleases(string current, string latest, bool expected)
        => Assert.AreEqual(expected, VersionInfo.IsUpdateAvailable(current, latest));
}
