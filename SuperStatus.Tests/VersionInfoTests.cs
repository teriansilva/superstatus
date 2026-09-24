using SuperStatus.Services.Updates;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #249 (epic #248): the build-version normalization + channel inference
/// behind <c>GET /api/version</c> and update detection. Pure logic, so it's
/// tested in isolation from the stamped assembly.
/// </summary>
[TestClass]
public class VersionInfoTests
{
    [TestMethod]
    [DataRow(null, "0.0.0-dev")]
    [DataRow("", "0.0.0-dev")]
    [DataRow("   ", "0.0.0-dev")]
    [DataRow("1.0.1", "1.0.1")]
    [DataRow("v1.0.1", "1.0.1")]
    [DataRow("V2.3.4", "2.3.4")]
    [DataRow("1.0.1+abc123", "1.0.1")]
    [DataRow("v1.2.0-rc.1+build.7", "1.2.0-rc.1")]
    [DataRow("edge", "edge")]
    public void Normalize_stripsPrefixAndBuildMetadata(string? raw, string expected)
        => Assert.AreEqual(expected, VersionInfo.Normalize(raw));

    [TestMethod]
    [DataRow("1.0.1", "latest")]
    [DataRow("v1.0.1", "latest")]
    [DataRow("1.2.0-rc.1", "latest")]      // a real pre-release release is still the latest channel
    [DataRow("edge", "edge")]
    [DataRow(null, "edge")]
    [DataRow("", "edge")]
    [DataRow("0.0.0-dev", "edge")]
    [DataRow("0.0.0", "edge")]
    [DataRow("1.2", "edge")]               // not a full SemVer
    [DataRow("not-a-version", "edge")]
    public void InferChannel_classifiesReleaseVsEdge(string? raw, string expected)
        => Assert.AreEqual(expected, VersionInfo.InferChannel(raw));

    [TestMethod]
    public void From_unstampedDevBuild_degradesToDevEdge()
    {
        var info = VersionInfo.From(null);
        Assert.AreEqual("0.0.0-dev", info.Version);
        Assert.AreEqual("edge", info.Channel);
    }

    [TestMethod]
    public void From_releaseTag_normalizesAndMarksLatest()
    {
        var info = VersionInfo.From("v1.0.1");
        Assert.AreEqual("1.0.1", info.Version);
        Assert.AreEqual("latest", info.Channel);
    }
}
