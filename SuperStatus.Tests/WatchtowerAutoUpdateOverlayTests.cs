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
    public void WatchtowerOverlay_enablesHttpApiTriggerForInAppUpdateButton()
    {
        // Issue #311: the overlay wires Watchtower's http-api (so the console's
        // "Update now" button can trigger a pull+restart) and hands the api the
        // trigger URL + shared token. The nightly schedule still runs alongside it.
        var overlay = ReadRepoFile("docker-compose.watchtower.yml");

        StringAssert.Contains(overlay, "WATCHTOWER_HTTP_API_UPDATE: \"true\"");
        StringAssert.Contains(overlay, "WATCHTOWER_HTTP_API_PERIODIC_POLLS: \"true\"");
        StringAssert.Contains(overlay, "SUPERSTATUS_UPDATE_TRIGGER_URL: http://watchtower:8080/v1/update");

        // The token must use the required (:?) expansion so the overlay fails fast
        // rather than coming up with a blank token (broken button + weakened auth).
        StringAssert.Contains(overlay, "WATCHTOWER_HTTP_API_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?");
        StringAssert.Contains(overlay, "SUPERSTATUS_UPDATE_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?");
        Assert.IsFalse(overlay.Contains("${SUPERSTATUS_UPDATE_TOKEN}", StringComparison.Ordinal),
            "token must use the required :? expansion, never a blank-able plain reference");

        // The http-api port must NOT be published to the host — the api reaches it
        // over the internal compose network only.
        Assert.IsFalse(overlay.Contains("8080:8080", StringComparison.Ordinal), "Watchtower http-api port must not be host-published");
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

    [TestMethod]
    public void Installer_generatesUpdateTokenAndHttpApiTrigger()
    {
        // Issue #311: the installer generates a Watchtower http-api token for the
        // "Update now" button, wires the http-api into the generated overlay, and
        // backfills the token idempotently on re-run (never regenerated).
        var installer = ReadRepoFile("install.sh");

        StringAssert.Contains(installer, "WATCHTOWER_HTTP_API_UPDATE: \"true\"");
        StringAssert.Contains(installer, "SUPERSTATUS_UPDATE_TRIGGER_URL: http://watchtower:8080/v1/update");
        // The generated overlay uses the fail-fast required token expansion.
        StringAssert.Contains(installer, "WATCHTOWER_HTTP_API_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?");
        // Fresh .env gets a generated token; re-run backfills only when missing.
        StringAssert.Contains(installer, "SUPERSTATUS_UPDATE_TOKEN=$(gen_secret)");
        StringAssert.Contains(installer, "if ! grep -q '^SUPERSTATUS_UPDATE_TOKEN=' .env");
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
