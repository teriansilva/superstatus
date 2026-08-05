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
    public void DemoCompose_EnablesDemoModeOnAllThreeAppServices()
    {
        var lines = File.ReadAllLines(FindRepoFile("docker-compose.demo.yml"));

        // Hardcoded rather than ${PUBLIC_DEMO} on purpose: an env-file value could be
        // flipped on a host without a code change, and demo-reset.sh greps for this
        // exact literal as its "this really is the demo stack" guard before `down -v`.
        // All three app services read PUBLIC_DEMO from their OWN environment:
        //   identity — seeds the demo admin + renders the login credentials panel
        //   web      — renders the site-wide banner + countdown
        //   api      — marks the instance onboarded so the public page isn't behind login
        // The API one is easy to forget precisely because it has no visible UI (#377);
        // omitting it silently leaves the public page redirecting to setup.
        CollectionAssert.Contains(ExtractService(lines, "identity"), "      PUBLIC_DEMO: \"true\"");
        CollectionAssert.Contains(ExtractService(lines, "web"), "      PUBLIC_DEMO: \"true\"");
        CollectionAssert.Contains(ExtractService(lines, "api"), "      PUBLIC_DEMO: \"true\"");
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

    [TestMethod]
    public void ResetService_RunsAsTheCheckoutOwner_NotRoot()
    {
        // Found the hard way on the first live run: as root, HOME=/root, so `git fetch`
        // against the private Forgejo repo fails with "could not read Username" and the
        // hourly reset silently stops happening. The credentials live in the checkout
        // owner's ~/.git-credentials. The unit must therefore drop to that user, and the
        // script must elevate only for the iptables guard.
        var unit = File.ReadAllText(FindRepoFile(Path.Combine(
            "scripts", "systemd", "superstatus-demo-reset.service")));
        var script = File.ReadAllText(FindRepoFile(Path.Combine("scripts", "demo-reset.sh")));

        StringAssert.Contains(unit, "User=marcusbraun",
            "The reset unit must run as the checkout owner; as root the git fetch cannot authenticate.");
        Assert.IsFalse(
            System.Text.RegularExpressions.Regex.IsMatch(unit, @"^\s*User\s*=\s*root\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            "The reset unit must not run as root.");

        StringAssert.Contains(script, "sudo -n \"${EXPECTED_DIR}/scripts/demo-egress-guard.sh\"",
            "The egress guard is the only step that needs elevation; reach it via sudo by absolute path "
            + "so a least-privilege sudoers rule can whitelist exactly this command.");
        StringAssert.Contains(script, "git ls-remote --exit-code origin HEAD",
            "The reset must fail fast with an actionable message when it cannot reach origin.");
    }

    [TestMethod]
    public void ProvisioningRunbook_DoesNotCreateRootOwnedCheckoutOrEnvFile()
    {
        // The unit runs as the service user, so a root-owned tree breaks it two ways: git
        // refuses it as "dubious ownership", and a root-owned 0600 .env.demo is unreadable
        // by `docker compose --env-file`. The runbook is the only thing standing between a
        // future operator and both failures, so it is asserted like code.
        var docs = File.ReadAllText(FindRepoFile(Path.Combine("docs", "deployment.md")));
        var provisioning = Section(docs, "### First-time provisioning");

        Assert.IsFalse(
            provisioning.Contains("sudo git clone", StringComparison.Ordinal),
            "The demo checkout must not be cloned as root — the service user needs to fetch into it.");
        Assert.IsFalse(
            provisioning.Contains("sudo cp .env.demo.example", StringComparison.Ordinal),
            "A root-owned 0600 .env.demo cannot be read by the service user's `docker compose --env-file`.");

        StringAssert.Contains(provisioning, "sudo install -d -o \"$USER\" -g \"$USER\" /opt/superstatus-demo",
            "The runbook must create the checkout owned by the service user.");

        // Least privilege: the service user gets passwordless sudo for the egress guard
        // ALONE, via a sudoers rule naming its absolute path — not blanket NOPASSWD: ALL.
        StringAssert.Contains(provisioning,
            "NOPASSWD: /opt/superstatus-demo/scripts/demo-egress-guard.sh",
            "The runbook must document the narrow, single-command sudoers rule for the egress guard.");
        StringAssert.Contains(provisioning, "sudo -n -l /opt/superstatus-demo/scripts/demo-egress-guard.sh",
            "The verification must probe the specific allowed command, not blanket `sudo -n true`.");
        Assert.IsFalse(provisioning.Contains("sudo -n true", StringComparison.Ordinal),
            "`sudo -n true` implies blanket passwordless root — the opposite of the least-privilege rule.");

        // And the reset must call the guard by the absolute path the sudoers rule names.
        var reset = File.ReadAllText(FindRepoFile(Path.Combine("scripts", "demo-reset.sh")));
        StringAssert.Contains(reset, "sudo -n \"${EXPECTED_DIR}/scripts/demo-egress-guard.sh\"",
            "demo-reset.sh must invoke the guard by absolute path so the narrow sudoers rule matches.");
    }

    /// <summary>Text from a markdown heading up to the next heading of the same level.</summary>
    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Heading '{heading}' not found in docs/deployment.md.");
        var next = markdown.IndexOf("\n### ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }

    [TestMethod]
    public void ApiStartup_MarksTheDemoOnboarded_SoThePublicPageIsNotBehindLogin()
    {
        // The demo seeds its admin directly into the identity DB, so it never runs the
        // setup wizard that stamps SiteSettings.OnboardedUtc. Home.razor redirects a
        // not-onboarded instance to /admin -> login, which would put the demo's public
        // status page behind a login wall after every hourly reset. The API must mark
        // the instance onboarded when PUBLIC_DEMO is on. (CompleteOnboardingAsync itself
        // is covered by SiteSettingsTests.Onboarding_NullUntilCompleted_ThenStampedOnce;
        // this asserts the demo-gated wiring in the composition root is present.)
        var program = File.ReadAllText(FindRepoFile(Path.Combine("SuperStatus.ApiService", "Program.cs")));
        var idx = program.IndexOf("DemoMode.IsEnabledFromEnvironment()", StringComparison.Ordinal);
        Assert.IsTrue(idx >= 0, "The API must branch on PUBLIC_DEMO at startup.");
        var region = program[idx..Math.Min(program.Length, idx + 400)];
        StringAssert.Contains(region, "CompleteOnboardingAsync",
            "Under PUBLIC_DEMO the API must mark the instance onboarded, or the demo's public page redirects to login.");

        // Both sides of the contract: the API reads PUBLIC_DEMO from its OWN environment,
        // so the demo compose must actually set it on the api service — otherwise the
        // branch above is dead code on the real stack (the bug this pair guards).
        var apiService = ExtractService(File.ReadAllLines(FindRepoFile("docker-compose.demo.yml")), "api");
        CollectionAssert.Contains(apiService, "      PUBLIC_DEMO: \"true\"",
            "Program.cs reads PUBLIC_DEMO in the API, but docker-compose.demo.yml doesn't set it on the api "
            + "service — the auto-onboard would never fire on the deployed demo.");
    }

    [TestMethod]
    public void EgressGuard_CoversBothTheForwardAndInputPaths_AndLetsRepliesBack()
    {
        // Two paths, learned live: a container -> a remote address is FORWARD-filtered by
        // DOCKER-USER, but a container -> the docker HOST'S own published port is accepted
        // by docker-proxy on INPUT and never sees DOCKER-USER. Without the INPUT chain the
        // demo reached prod (8190) and staging (8192). And the FORWARD chain needs an
        // ESTABLISHED,RELATED accept first, or the reply to an inbound proxied request
        // (to the LB on an ephemeral port) is caught by the 192.168/16 reject and every
        // request through the reverse proxy times out.
        var guard = File.ReadAllText(FindRepoFile(Path.Combine("scripts", "demo-egress-guard.sh")));

        StringAssert.Contains(guard, "SS-DEMO-INPUT", "The guard must also filter the INPUT path (docker-proxy to the host).");
        StringAssert.Contains(guard, "iptables -I INPUT",
            "The guard must jump into INPUT for the demo bridge, or demo->host published ports bypass it.");

        // Anchor on the APPLIED rules, not the PRIVATE_RANGES array literal near the top:
        // the ESTABLISHED accept must precede the reject loop that consumes ${range}.
        var estIdx = guard.IndexOf("ctstate ESTABLISHED,RELATED -j RETURN", StringComparison.Ordinal);
        var rejLoopIdx = guard.IndexOf("-d \"${range}\"", StringComparison.Ordinal);
        Assert.IsTrue(estIdx >= 0, "The egress chain must accept ESTABLISHED,RELATED replies.");
        Assert.IsTrue(rejLoopIdx >= 0, "The egress chain must reject the private ranges in a loop.");
        Assert.IsTrue(estIdx < rejLoopIdx,
            "ESTABLISHED,RELATED must be accepted BEFORE the private-range reject, or inbound proxy replies are dropped.");

        // The deployment runbook must describe BOTH chains, or an operator misses the
        // load-bearing INPUT fix for the demo→docker-host (prod/staging) hole.
        var egressDocs = Section(File.ReadAllText(FindRepoFile(Path.Combine("docs", "deployment.md"))),
            "### Egress restriction");
        StringAssert.Contains(egressDocs, "SS-DEMO-INPUT",
            "docs/deployment.md must document the INPUT chain, not just SS-DEMO-EGRESS.");
        StringAssert.Contains(egressDocs, "docker-proxy",
            "docs must explain why DOCKER-USER alone misses demo→host traffic.");
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
