namespace SuperStatus.Tests;

/// <summary>
/// Issue #334: the Watchtower update engine ships in the DEFAULT self-host stack,
/// so "Update now" and the auto-update schedule work from the web console without
/// server access. These tests pin the deployment contract that makes that safe:
///
///   * Only Watchtower mounts the Docker socket — web/api never do.
///   * Watchtower is an on-demand executor (http-api, no schedule, no polls), so
///     the app is the single scheduler and "auto-update off" really means off.
///   * The engine is opt-out-able (<c>--no-updater</c> / SUPERSTATUS_UPDATE_ENGINE=none),
///     and opting out removes the service entirely rather than just hiding a button.
///   * The shared http-api token uses the fail-fast <c>:?</c> expansion and is never
///     host-published or blank-able.
/// </summary>
[TestClass]
public class WatchtowerUpdateEngineTests
{
    [TestMethod]
    public void BaseCompose_labelsAppContainersButShipsNoUpdaterAndNoSocket()
    {
        var compose = ReadRepoFile("docker-compose.yml");

        // The app containers opt into Watchtower's label filter + scope...
        StringAssert.Contains(compose, "com.centurylinklabs.watchtower.enable: \"true\"");
        StringAssert.Contains(compose, "com.centurylinklabs.watchtower.scope: \"superstatus\"");

        // ...but postgres does not — the updater only ever touches app containers.
        var postgresBlock = Between(compose, "  postgres:", "  identity:");
        Assert.IsFalse(postgresBlock.Contains("com.centurylinklabs.watchtower.enable", StringComparison.Ordinal));

        // The base file never defines the updater and never mounts the socket; that
        // lives in docker-compose.watchtower.yml, which .env's COMPOSE_FILE layers on.
        // This is what keeps `docker compose up -d` in a plain checkout (and the
        // pr-dev-env stack) socket-free.
        Assert.IsFalse(compose.Contains("/var/run/docker.sock", StringComparison.Ordinal),
            "the base compose file must never mount the Docker socket");
        Assert.IsFalse(compose.Contains("\n  watchtower:", StringComparison.Ordinal),
            "the watchtower service belongs in docker-compose.watchtower.yml");

        // #334: auto-update is a persisted setting now, not a display-only env flag.
        Assert.IsFalse(compose.Contains("SUPERSTATUS_AUTOUPDATE", StringComparison.Ordinal),
            "SUPERSTATUS_AUTOUPDATE is retired — the toggle lives in SiteSettings");
    }

    [TestMethod]
    public void UpdateEngine_isOnDemandOnly_soTheAppIsTheSoleScheduler()
    {
        var engine = ReadRepoFile("docker-compose.watchtower.yml");

        StringAssert.Contains(engine, "WATCHTOWER_HTTP_API_UPDATE: \"true\"");

        // The whole point of #334: the app owns the cadence. A Watchtower schedule or
        // a periodic poll would apply updates behind the console's back — including
        // while the operator has auto-update switched OFF. Assert on real YAML keys:
        // the comments deliberately *name* both vars to explain their absence.
        Assert.IsFalse(DeclaresEnvKey(engine, "WATCHTOWER_SCHEDULE"),
            "Watchtower must not self-schedule; AutoUpdateWorker owns the cadence");
        Assert.IsFalse(DeclaresEnvKey(engine, "WATCHTOWER_HTTP_API_PERIODIC_POLLS"),
            "periodic polls would re-introduce a second scheduler");
    }

    [TestMethod]
    public void UpdateEngine_onlyWatchtowerHoldsTheSocket_andTheTokenCannotBeBlank()
    {
        var engine = ReadRepoFile("docker-compose.watchtower.yml");

        StringAssert.Contains(engine, "image: containrrr/watchtower:latest");
        StringAssert.Contains(engine, "WATCHTOWER_LABEL_ENABLE: \"true\"");
        StringAssert.Contains(engine, "WATCHTOWER_SCOPE: \"superstatus\"");
        StringAssert.Contains(engine, "WATCHTOWER_CLEANUP: \"true\"");
        StringAssert.Contains(engine, "root-equivalent access");

        // The socket mount appears exactly once, and it is under the watchtower
        // service — the api block only ever receives the trigger URL + token.
        Assert.AreEqual(1, CountOccurrences(engine, "/var/run/docker.sock:/var/run/docker.sock"),
            "exactly one service may mount the Docker socket");
        var apiBlock = Between(engine, "  api:", "  watchtower:");
        Assert.IsFalse(apiBlock.Contains("docker.sock", StringComparison.Ordinal),
            "the api container must never mount the Docker socket");
        StringAssert.Contains(apiBlock, "SUPERSTATUS_UPDATE_TRIGGER_URL: http://watchtower:8080/v1/update");

        // Both sides of the shared secret use the required (:?) expansion so the
        // stack fails fast rather than coming up with a blank token (broken button +
        // an unauthenticated http-api).
        StringAssert.Contains(engine, "WATCHTOWER_HTTP_API_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?");
        StringAssert.Contains(engine, "SUPERSTATUS_UPDATE_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?");
        Assert.IsFalse(engine.Contains("${SUPERSTATUS_UPDATE_TOKEN}", StringComparison.Ordinal),
            "token must use the required :? expansion, never a blank-able plain reference");

        // The http-api port stays on the internal compose network.
        Assert.IsFalse(engine.Contains("8080:8080", StringComparison.Ordinal),
            "Watchtower http-api port must not be host-published");
    }

    [TestMethod]
    public void Installer_shipsTheEngineByDefault_wiredThroughComposeFile()
    {
        var installer = ReadRepoFile("install.sh");

        // Default engine + the COMPOSE_FILE that makes the bare day-2 commands
        // (`docker compose pull && docker compose up -d`) include the updater.
        StringAssert.Contains(installer, "UPDATE_ENGINE=watchtower");
        StringAssert.Contains(installer, "COMPOSE_FILE_VALUE=\"docker-compose.yml:docker-compose.watchtower.yml\"");
        StringAssert.Contains(installer, "env_upsert COMPOSE_FILE \"$COMPOSE_FILE_VALUE\"");
        StringAssert.Contains(installer, "env_upsert SUPERSTATUS_UPDATE_ENGINE \"$UPDATE_ENGINE\"");

        // It writes the engine compose file, on-demand-only.
        StringAssert.Contains(installer, "cat > docker-compose.watchtower.yml");
        StringAssert.Contains(installer, "WATCHTOWER_HTTP_API_UPDATE: \"true\"");
        StringAssert.Contains(installer, "SUPERSTATUS_UPDATE_TRIGGER_URL: http://watchtower:8080/v1/update");
        StringAssert.Contains(installer, "WATCHTOWER_HTTP_API_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?");

        // The generated engine file must not re-introduce a second scheduler.
        var emitted = Between(installer, "cat > docker-compose.watchtower.yml", "WATCHTOWER_COMPOSE_EOF");
        Assert.IsFalse(DeclaresEnvKey(emitted, "WATCHTOWER_SCHEDULE"),
            "the generated engine file must not self-schedule");
        Assert.IsFalse(DeclaresEnvKey(emitted, "WATCHTOWER_HTTP_API_PERIODIC_POLLS"),
            "the generated engine file must not poll");
    }

    [TestMethod]
    public void Installer_optOut_omitsTheEngineEntirely()
    {
        var installer = ReadRepoFile("install.sh");

        // --no-updater / SUPERSTATUS_UPDATE_ENGINE=none drop the engine file from
        // COMPOSE_FILE, so the service is never created and nothing mounts the
        // socket. The api then has no trigger URL ⇒ CanApplyInApp=false ⇒ the panel
        // falls back to the guided command.
        StringAssert.Contains(installer, "--no-updater");
        StringAssert.Contains(installer, "UPDATE_ENGINE=none");
        StringAssert.Contains(installer, "COMPOSE_FILE_VALUE=\"docker-compose.yml\"");
        StringAssert.Contains(installer, "SUPERSTATUS_UPDATE_ENGINE must be 'watchtower' or 'none'.");

        // A re-run with --no-updater has to actually tear down a Watchtower left
        // behind by an earlier default install.
        StringAssert.Contains(installer, "up -d --remove-orphans");
    }

    [TestMethod]
    public void Installer_deprecatesAutoUpdateFlags_withoutBreakingThem()
    {
        var installer = ReadRepoFile("install.sh");

        // Auto-update is a runtime toggle now. The old flags stay accepted (existing
        // scripts/docs keep working) but choose nothing.
        StringAssert.Contains(installer, "--auto-update|--no-auto-update)");
        StringAssert.Contains(installer, "is deprecated and ignored");

        // The retired env marker must not be written into .env any more.
        Assert.IsFalse(installer.Contains("SUPERSTATUS_AUTOUPDATE=watchtower", StringComparison.Ordinal),
            "the SUPERSTATUS_AUTOUPDATE marker is retired");
    }

    [TestMethod]
    public void Installer_generatesUpdateTokenOnceAndNeverRegeneratesIt()
    {
        // The api ↔ Watchtower shared secret must survive a re-run, or the button
        // silently starts 401-ing. Fresh .env gets a generated token; an existing
        // one is only backfilled when missing.
        var installer = ReadRepoFile("install.sh");

        StringAssert.Contains(installer, "SUPERSTATUS_UPDATE_TOKEN=$(gen_secret)");
        StringAssert.Contains(installer, "if ! grep -q '^SUPERSTATUS_UPDATE_TOKEN=' .env");
    }

    /// <summary>True when the YAML actually declares <paramref name="key"/> as an
    /// environment entry (<c>KEY: value</c>), ignoring comment lines — which name the
    /// retired vars on purpose, to document why they're gone.</summary>
    private static bool DeclaresEnvKey(string yaml, string key)
        => yaml.Split('\n')
            .Select(line => line.TrimStart())
            .Any(line => !line.StartsWith('#') && line.StartsWith(key + ":", StringComparison.Ordinal));

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
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
