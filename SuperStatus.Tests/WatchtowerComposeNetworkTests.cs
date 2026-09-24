using YamlDotNet.Serialization;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #380: the update engine only works if <c>api</c> can reach <c>watchtower</c> by
/// service name on the Compose network. It couldn't: <c>watchtower</c> declared no
/// <c>networks:</c>, so Compose attached it to the implicit <c>default</c> network while
/// <c>api</c> stayed on <c>internal</c>. Both "Update now" and the scheduled auto-update
/// failed DNS resolution on every default install.
///
/// <see cref="WatchtowerUpdateEngineTests"/> asserts on file *text*, which is exactly why
/// it could not see this: a missing key has no text. These tests assert on the *rendered
/// service graph* — the YAML parsed and Compose's default-network rule applied — so a
/// future overlay edit that separates the two services fails here.
/// </summary>
[TestClass]
public class WatchtowerComposeNetworkTests
{
    /// <summary>Compose's rule: a service with no <c>networks:</c> key joins <c>default</c>.</summary>
    private const string ImplicitNetwork = "default";

    [TestMethod]
    public void ApiAndWatchtower_shareANetwork_soTheTriggerResolves()
    {
        var services = RenderDefaultStack();

        var api = NetworksOf(services, "api");
        var watchtower = NetworksOf(services, "watchtower");

        CollectionAssert.AreEquivalent(new[] { "internal" }, watchtower.ToArray(),
            "watchtower must join the app network, not Compose's implicit `default`");

        var shared = api.Intersect(watchtower).ToArray();
        Assert.AreNotEqual(0, shared.Length,
            $"api ({string.Join(",", api)}) and watchtower ({string.Join(",", watchtower)}) share no network — "
            + "http://watchtower:8080/v1/update cannot resolve, so in-app updates are dead");
    }

    [TestMethod]
    public void Watchtower_httpApiIsNeverPublishedToTheHost()
    {
        // Joining `internal` must not turn into exposing the (token-authenticated, but
        // root-equivalent) http-api on the host. It stays reachable only in-network.
        var services = RenderDefaultStack();

        Assert.IsFalse(services["watchtower"].ContainsKey("ports"),
            "the Watchtower http-api port must never be published to the host");
    }

    [TestMethod]
    public void InstallerEmittedOverlay_hasTheSameNetworkWiringAsTheCheckedInOne()
    {
        // The installer writes its own copy of the overlay. The two must not drift — a
        // fresh self-host install is exactly the case #380 broke.
        var checkedIn = ParseServices(ReadRepoFile("docker-compose.watchtower.yml"));
        var emitted = ParseServices(EmittedWatchtowerOverlay());

        CollectionAssert.AreEquivalent(
            NetworksOf(checkedIn, "watchtower").ToArray(),
            NetworksOf(emitted, "watchtower").ToArray(),
            "install.sh's generated overlay must wire watchtower onto the same network");

        Assert.IsFalse(emitted["watchtower"].ContainsKey("ports"),
            "the generated overlay must not publish the http-api either");
    }

    [TestMethod]
    public void BaseStack_putsEveryAppServiceOnTheInternalNetwork()
    {
        // Guards the other half of the invariant: if `api` ever left `internal`, the
        // shared-network assertion above could pass for the wrong reason.
        var services = ParseServices(ReadRepoFile("docker-compose.yml"));

        foreach (var name in new[] { "api", "web", "identity", "postgres" })
            CollectionAssert.Contains(NetworksOf(services, name).ToArray(), "internal", $"{name} must be on `internal`");
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    /// <summary>
    /// The default self-host stack as Compose renders it: the base file with the update
    /// engine layered on (which is what .env's COMPOSE_FILE selects). Service maps are
    /// merged key-by-key, which is all this assertion needs.
    /// </summary>
    private static Dictionary<string, Dictionary<string, object>> RenderDefaultStack()
    {
        var merged = ParseServices(ReadRepoFile("docker-compose.yml"));

        foreach (var (name, overlay) in ParseServices(ReadRepoFile("docker-compose.watchtower.yml")))
        {
            if (!merged.TryGetValue(name, out var existing))
            {
                merged[name] = overlay;
                continue;
            }
            foreach (var (key, value) in overlay)
                existing[key] = value;
        }

        return merged;
    }

    /// <summary>Effective networks for a service, applying Compose's implicit-`default` rule.</summary>
    private static IEnumerable<string> NetworksOf(Dictionary<string, Dictionary<string, object>> services, string name)
    {
        Assert.IsTrue(services.ContainsKey(name), $"service `{name}` is missing from the rendered stack");

        if (!services[name].TryGetValue("networks", out var networks))
            return [ImplicitNetwork];

        // Both Compose forms: a `[internal]` sequence, or an `internal:` mapping.
        return networks switch
        {
            List<object> seq => seq.Select(n => n?.ToString() ?? string.Empty),
            Dictionary<object, object> map => map.Keys.Select(k => k?.ToString() ?? string.Empty),
            _ => throw new AssertFailedException($"unexpected `networks` shape on `{name}`: {networks?.GetType()}"),
        };
    }

    private static Dictionary<string, Dictionary<string, object>> ParseServices(string yaml)
    {
        var root = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);
        Assert.IsTrue(root.ContainsKey("services"), "compose file has no `services` block");

        var services = (Dictionary<object, object>)root["services"];
        return services.ToDictionary(
            s => s.Key.ToString()!,
            s => ((Dictionary<object, object>)s.Value).ToDictionary(k => k.Key.ToString()!, k => k.Value));
    }

    /// <summary>The docker-compose.watchtower.yml that install.sh writes, lifted out of its heredoc.</summary>
    private static string EmittedWatchtowerOverlay()
    {
        const string open = "cat > docker-compose.watchtower.yml <<'WATCHTOWER_COMPOSE_EOF'\n";
        const string close = "\nWATCHTOWER_COMPOSE_EOF";

        var installer = ReadRepoFile("install.sh");
        var start = installer.IndexOf(open, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, "install.sh no longer emits docker-compose.watchtower.yml via that heredoc");
        start += open.Length;

        var end = installer.IndexOf(close, start, StringComparison.Ordinal);
        Assert.IsTrue(end > start, "unterminated WATCHTOWER_COMPOSE_EOF heredoc");
        return installer[start..end];
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SuperStatus.sln")))
            dir = dir.Parent;

        Assert.IsNotNull(dir, "Could not locate repo root from test output directory.");
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }
}
