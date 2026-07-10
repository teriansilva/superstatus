namespace SuperStatus.Tests;

/// <summary>
/// Issue #377 — the containment tests for the public demo instance.
///
/// <para>The whole feature rests on one claim: <c>PUBLIC_DEMO</c> is set by the demo
/// compose file and by nothing else. If it ever leaks into staging, production, the
/// self-host compose, or the installer, a real deployment silently gains a
/// publicly-documented administrator with a five-character password. These tests are
/// what stop that from happening quietly.</para>
/// </summary>
[TestClass]
public class DemoComposeTests
{
    private const string Flag = "PUBLIC_DEMO";

    /// <summary>
    /// Every deployment surface that must never enable demo mode. A new compose file or
    /// installer that grows a demo flag should fail here first.
    /// </summary>
    private static readonly string[] NonDemoSurfaces =
    [
        "docker-compose.yml",
        "docker-compose.staging.yml",
        "docker-compose.prod.yml",
        "docker-compose.landing.yml",
        "docker-compose.pr-dev.yml",
        ".env.example",
        ".env.staging",
        ".env.production",
        ".env.demo.example",
        "install.sh",
    ];

    [TestMethod]
    public void PublicDemoFlag_AppearsInNoSurfaceOtherThanTheDemoCompose()
    {
        foreach (var surface in NonDemoSurfaces)
        {
            var text = File.ReadAllText(FindRepoFile(surface));
            Assert.IsFalse(
                text.Contains(Flag, StringComparison.Ordinal),
                $"{surface} mentions {Flag}. Demo mode seeds a well-known weak admin credential and " +
                $"advertises it on the login page; it must exist only in docker-compose.demo.yml.");
        }
    }

    [TestMethod]
    public void DemoCompose_EnablesDemoModeOnIdentityAndWeb()
    {
        var lines = File.ReadAllLines(FindRepoFile("docker-compose.demo.yml"));

        // Hardcoded rather than ${PUBLIC_DEMO} on purpose: an env-file value could be
        // flipped on a host without a code change, and demo-reset.sh greps for this
        // exact literal as its "this really is the demo stack" guard before `down -v`.
        CollectionAssert.Contains(ExtractService(lines, "identity"), "      PUBLIC_DEMO: \"true\"");
        CollectionAssert.Contains(ExtractService(lines, "web"), "      PUBLIC_DEMO: \"true\"");

        // The API never reads the flag — it has no login page and no banner.
        CollectionAssert.DoesNotContain(ExtractService(lines, "api"), "      PUBLIC_DEMO: \"true\"");
    }

    [TestMethod]
    public void DemoCompose_UsesItsOwnProjectPortsAndVolumes()
    {
        var text = File.ReadAllText(FindRepoFile("docker-compose.demo.yml"));

        // The project name namespaces the volumes that demo-reset.sh destroys hourly.
        // If this drifts, `down -v` starts aiming at another project's data.
        StringAssert.Contains(text, "name: superstatus-demo");

        // 8190-8194 belong to prod (8190/8191), staging (8192/8193) and landing (8194).
        StringAssert.Contains(text, "\"8196:8080\"");
        StringAssert.Contains(text, "\"8195:8080\"");
        Assert.IsFalse(text.Contains("\"8190:", StringComparison.Ordinal), "8190 is production web.");
        Assert.IsFalse(text.Contains("\"8192:", StringComparison.Ordinal), "8192 is staging web.");
        Assert.IsFalse(text.Contains("\"8194:", StringComparison.Ordinal), "8194 is the landing page.");
    }

    [TestMethod]
    public void DemoCompose_PinsTheBridgeNameAndSubnetTheEgressGuardTargets()
    {
        var text = File.ReadAllText(FindRepoFile("docker-compose.demo.yml"));
        var guard = File.ReadAllText(FindRepoFile(Path.Combine("scripts", "demo-egress-guard.sh")));

        // The hourly `down -v` recreates the network. Without a pinned name docker
        // generates br-<hash>, and the iptables rules the guard installed would point at
        // an interface that no longer exists — silently reopening the SSRF path to the LAN.
        StringAssert.Contains(text, "com.docker.network.bridge.name: ss-demo0");
        StringAssert.Contains(guard, "readonly BRIDGE=\"ss-demo0\"");

        // 10/8 is unused by the LAN and by every other compose project on the docker host,
        // which all sit inside the 172.16/12 and 192.168/16 ranges the guard rejects.
        StringAssert.Contains(text, "subnet: 10.77.240.0/24");
    }

    [TestMethod]
    public void DemoCompose_TracksTheLastReleaseNotMainBuilds()
    {
        var text = File.ReadAllText(FindRepoFile("docker-compose.demo.yml"));

        // The demo showcases shipped software. :edge would make it a live preview of
        // trunk — a deliberate non-goal (see the issue's "Out of scope").
        StringAssert.Contains(text, "${SUPERSTATUS_VERSION:-latest}");
        Assert.IsFalse(
            text.Contains("${SUPERSTATUS_VERSION:-edge}", StringComparison.Ordinal),
            "The demo must default to the last release (:latest), not to :edge.");
    }

    [TestMethod]
    public void ResetScript_GuardsTheDestructiveStepAndPullsBeforeDestroying()
    {
        var script = File.ReadAllText(FindRepoFile(Path.Combine("scripts", "demo-reset.sh")));

        // The project name must be a literal. If it ever becomes "$1" or "${PROJECT:-...}",
        // a caller could aim `down -v` at superstatus-prod.
        StringAssert.Contains(script, "readonly PROJECT=\"superstatus-demo\"");

        // Ordering invariant: everything fallible (config validation, image pull) happens
        // before the volumes are destroyed, so a bad release leaves the demo up.
        var pullIndex = script.IndexOf("pull --quiet", StringComparison.Ordinal);
        var downIndex = script.IndexOf("down --volumes", StringComparison.Ordinal);
        Assert.IsTrue(pullIndex > 0, "reset script must pull images");
        Assert.IsTrue(downIndex > 0, "reset script must destroy volumes");
        Assert.IsTrue(
            pullIndex < downIndex,
            "The image pull must precede `down --volumes`. Destroying the database before "
            + "confirming the new images exist leaves the demo unrecoverable when a pull fails.");
    }

    private static string FindRepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {name} from {AppContext.BaseDirectory}.");
    }

    private static List<string> ExtractService(string[] lines, string serviceName)
    {
        var start = Array.FindIndex(lines, line => line == $"  {serviceName}:");
        if (start < 0)
        {
            Assert.Fail($"Service '{serviceName}' not found in docker-compose.demo.yml.");
        }

        var end = Array.FindIndex(lines, start + 1, line =>
            line.Length > 0 && !char.IsWhiteSpace(line[0]));
        if (end < 0)
        {
            end = lines.Length;
        }

        // A two-space-indented key at the same level as the service starts the next one.
        var next = Array.FindIndex(lines, start + 1, end - start - 1, line =>
            line.StartsWith("  ", StringComparison.Ordinal)
            && !line.StartsWith("   ", StringComparison.Ordinal)
            && line.TrimEnd().EndsWith(':'));

        return lines[(start + 1)..(next > 0 ? next : end)].ToList();
    }
}
