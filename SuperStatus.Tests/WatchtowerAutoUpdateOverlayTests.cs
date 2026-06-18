namespace SuperStatus.Tests;

/// <summary>
/// Epic #248 Phase 2: self-host automatic updates are an opt-in Watchtower
/// overlay. These tests pin the deployment contract so the app containers stay
/// scoped/label-filtered and the base stack remains manual-update by default.
/// </summary>
[TestClass]
public class WatchtowerAutoUpdateOverlayTests
{
    [TestMethod]
    public void BaseCompose_labelsOnlyAppContainersForWatchtower()
    {
        var compose = ReadRepoFile("docker-compose.yml");

        StringAssert.Contains(compose, "com.centurylinklabs.watchtower.enable: \"true\"");
        StringAssert.Contains(compose, "com.centurylinklabs.watchtower.scope: \"superstatus\"");
        StringAssert.Contains(compose, "SUPERSTATUS_AUTOUPDATE: ${SUPERSTATUS_AUTOUPDATE:-}");

        var postgresBlock = Between(compose, "  postgres:", "  identity:");
        Assert.IsFalse(postgresBlock.Contains("com.centurylinklabs.watchtower.enable", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WatchtowerOverlay_isScopedLabelFilteredAndAdvertisesStatus()
    {
        var overlay = ReadRepoFile("docker-compose.watchtower.yml");

        StringAssert.Contains(overlay, "image: containrrr/watchtower:latest");
        StringAssert.Contains(overlay, "SUPERSTATUS_AUTOUPDATE: watchtower");
        StringAssert.Contains(overlay, "WATCHTOWER_LABEL_ENABLE: \"true\"");
        StringAssert.Contains(overlay, "WATCHTOWER_SCOPE: \"superstatus\"");
        StringAssert.Contains(overlay, "WATCHTOWER_CLEANUP: \"true\"");
        StringAssert.Contains(overlay, "WATCHTOWER_SCHEDULE: \"${WATCHTOWER_SCHEDULE:-0 0 3 * * *}\"");
        StringAssert.Contains(overlay, "/var/run/docker.sock:/var/run/docker.sock");
        StringAssert.Contains(overlay, "root-equivalent access");
    }

    [TestMethod]
    public void Installer_supportsAutoUpdateFlagAndGeneratedOverlay()
    {
        var installer = ReadRepoFile("install.sh");

        StringAssert.Contains(installer, "--auto-update");
        StringAssert.Contains(installer, "cat > docker-compose.watchtower.yml");
        StringAssert.Contains(installer, "SUPERSTATUS_AUTOUPDATE=watchtower");
        StringAssert.Contains(installer, "COMPOSE_FILES=\"$COMPOSE_FILES -f docker-compose.watchtower.yml\"");
        StringAssert.Contains(installer, "WATCHTOWER_LABEL_ENABLE: \"true\"");
        StringAssert.Contains(installer, "WATCHTOWER_SCOPE: \"superstatus\"");
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SuperStatus.sln")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, "Could not locate repo root from test output directory.");
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }

    private static string Between(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, $"Could not find start marker {start}");
        var endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(endIndex > startIndex, $"Could not find end marker {end}");
        return text[startIndex..endIndex];
    }
}
