using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperStatus.ApiService;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Plugins;
using SuperStatus.Services.Providers;

namespace SuperStatus.Tests;

/// <summary>
/// #433 — the dry-run probe endpoint (<c>POST /statuscheck/providers/{type}/test</c>).
///
/// The interesting cases are not "does a probe run" but the four invariants that make it safe
/// to point at an operator-supplied host with an operator's stored credential:
/// the stored config is only read for a MATCHING provider type, provider exception text never
/// reaches the response, a provider that ignores cancellation cannot pin the request, and
/// nothing is persisted.
/// </summary>
[TestClass]
public class CheckProviderTestApiTests
{
    // ---- fakes ---------------------------------------------------------------

    private sealed class FakeProvider : ICheckProvider
    {
        private readonly Func<ProbeContext, CancellationToken, Task<ProbeResult>> _probe;
        public FakeProvider(ProviderDescriptor descriptor, Func<ProbeContext, CancellationToken, Task<ProbeResult>> probe)
        {
            Descriptor = descriptor;
            _probe = probe;
        }
        public ProviderDescriptor Descriptor { get; }
        public string? LastConfigJson { get; private set; }
        public Task<ProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken = default)
        {
            LastConfigJson = context.ConfigJson;
            return _probe(context, cancellationToken);
        }
    }

    private sealed class FakeRegistry(params ICheckProvider[] providers) : ICheckProviderRegistry
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => providers.Select(p => p.Descriptor).ToList();
        public string DefaultTypeId => providers.FirstOrDefault()?.Descriptor.TypeId ?? "http";
        public ICheckProvider? Find(string? typeId) => providers.FirstOrDefault(p => p.Descriptor.TypeId == typeId);
        public ICheckProvider Resolve(string? typeId) => Find(typeId) ?? providers[0];
    }

    private static ProviderDescriptor Descriptor(
        string typeId, ProbeDirection direction = ProbeDirection.Pull,
        TimeSpan? probeTimeout = null, IReadOnlyList<MetricDef>? metrics = null) =>
        new(typeId, $"{typeId.ToUpperInvariant()} provider", "icon",
            new ConfigSchema(1,
            [
                new("url", "URL", ConfigFieldKind.Text, Required: true),
                new("apiKey", "API key", ConfigFieldKind.Secret, Required: false),
            ]),
            metricDefs: metrics,
            probeTimeout: probeTimeout,
            direction: direction,
            // Real providers declare the field carrying their target (http -> url,
            // ai -> baseUrl). The stored-secret guard needs it to prove the endpoint is
            // unchanged, and fails closed without it — see the no-target-field test below.
            batchTargetField: "url");

    /// <summary>A real repository over an in-memory SQLite DB — same harness the #365
    /// notification-test suite uses, so the stored-config lookup is exercised through the
    /// actual query rather than a hand-written stub that could drift from it.</summary>
    private static SuperStatusDb NewDb(SqliteConnection conn)
    {
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static Repository<StatusCheck> SeededRepo(SuperStatusDb db, params StatusCheck[] checks)
    {
        foreach (var c in checks)
        {
            c.Title ??= "seed";
            c.StatusCheckUrl ??= "https://seed.example/health";   // NOT NULL in the schema
            c.ServiceLogoUrl ??= string.Empty;
            db.StatusCheckSet.Add(c);
        }
        db.SaveChanges();
        return new Repository<StatusCheck>(db);
    }

    private static CheckTestRequest Request(long checkId = 0, params (string Key, string Value)[] config) => new()
    {
        CheckId = checkId,
        Config = config.ToDictionary(c => c.Key, c => c.Value),
    };

    // ---- happy path ----------------------------------------------------------

    [TestMethod]
    public async Task ValidConfig_RunsTheProbe_AndMapsDeclaredMetricsWithTheirLabels()
    {
        var provider = new FakeProvider(
            Descriptor("ai", metrics: [new("ttft_ms", "TTFT", "ms", MetricKind.Gauge), new("tokens_per_sec", "Throughput", "tok/s", MetricKind.Gauge)]),
            (_, _) => Task.FromResult(new ProbeResult
            {
                FailType = FailType.NoFail,
                LatencyMs = 988,
                Reachable = true,
                MetricsJson = """{"ttft_ms":412,"tokens_per_sec":63.4,"undeclared":99}""",
            }));

        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var outcome = await CheckProviderTestApi.RunAsync("ai", Request(config: ("url", "https://ai.example/v1")),
            new FakeRegistry(provider), SeededRepo(db), CancellationToken.None);

        Assert.AreEqual(CheckProviderTestApi.CheckTestStatus.Ok, outcome.Status);
        Assert.AreEqual("up", outcome.Body!.Outcome);
        Assert.IsTrue(outcome.Body.Ok);
        Assert.AreEqual(988, outcome.Body.LatencyMs);

        // Declared metrics come back labelled; an UNDECLARED key is dropped — a provider may
        // only report what its descriptor promised.
        CollectionAssert.AreEquivalent(new[] { "ttft_ms", "tokens_per_sec" }, outcome.Body.Metrics.Select(m => m.Key).ToArray());
        var ttft = outcome.Body.Metrics.Single(m => m.Key == "ttft_ms");
        Assert.AreEqual("TTFT", ttft.Label);
        Assert.AreEqual("ms", ttft.Unit);
        Assert.AreEqual(412, ttft.Value);
    }

    [TestMethod]
    public async Task UnknownProvider_IsNotFound()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var outcome = await CheckProviderTestApi.RunAsync("nope", Request(),
            new FakeRegistry(new FakeProvider(Descriptor("http"), (_, _) => Task.FromResult(ProbeResult.Unreachable()))),
            SeededRepo(db), CancellationToken.None);

        Assert.AreEqual(CheckProviderTestApi.CheckTestStatus.UnknownProvider, outcome.Status);
    }

    [TestMethod]
    public async Task PushProvider_CannotBeTested()
    {
        var provider = new FakeProvider(Descriptor("heartbeat", ProbeDirection.Push),
            (_, _) => Task.FromResult(ProbeResult.Unreachable()));

        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var outcome = await CheckProviderTestApi.RunAsync("heartbeat", Request(config: ("url", "https://x")),
            new FakeRegistry(provider), SeededRepo(db), CancellationToken.None);

        Assert.AreEqual(CheckProviderTestApi.CheckTestStatus.NotTestable, outcome.Status);
        StringAssert.Contains(outcome.Message!, "push check");
    }

    [TestMethod]
    public async Task InvalidConfig_Returns422WithTheSchemaReason_AndNeverProbes()
    {
        bool probed = false;
        var provider = new FakeProvider(Descriptor("http"), (_, _) => { probed = true; return Task.FromResult(ProbeResult.Unreachable()); });

        // "url" is required by the schema and not supplied.
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var outcome = await CheckProviderTestApi.RunAsync("http", Request(),
            new FakeRegistry(provider), SeededRepo(db), CancellationToken.None);

        Assert.AreEqual(CheckProviderTestApi.CheckTestStatus.InvalidConfig, outcome.Status);
        StringAssert.Contains(outcome.Message!, "URL");
        Assert.IsFalse(probed, "an invalid config must be rejected before any outbound request");
    }

    // ---- the stored-secret rule ---------------------------------------------

    /// <summary>Blank secret keeps the stored credential — for the SAME endpoint. This test
    /// originally retargeted the URL while expecting the key to carry over; that combination is
    /// now precisely the exfiltration case and is covered separately below, so the target here
    /// stays put and only the path varies.</summary>
    [TestMethod]
    public async Task BlankSecret_ResolvesToTheStoredOne_WhenTheCheckIsTheSameProviderType()
    {
        var provider = new FakeProvider(Descriptor("ai"), (_, _) => Task.FromResult(ProbeResult.Http(FailType.NoFail, 5, 200)));
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = SeededRepo(db, new StatusCheck
        {
            Title = "stored ai check",
            ProviderType = "ai",
            ConfigJson = """{"schemaVersion":1,"url":"https://stored.example","apiKey":"stored-secret"}""",
        });
        long id = db.StatusCheckSet.Single().Id;

        await CheckProviderTestApi.RunAsync("ai", Request(id, ("url", "https://stored.example/typed-path")),
            new FakeRegistry(provider), repo, CancellationToken.None);

        using var doc = JsonDocument.Parse(provider.LastConfigJson!);
        Assert.AreEqual("stored-secret", doc.RootElement.GetProperty("apiKey").GetString(),
            "a blank secret keeps the stored credential (the 'leave blank to keep' rule)");
        Assert.AreEqual("https://stored.example/typed-path", doc.RootElement.GetProperty("url").GetString(),
            "…while typed non-secret values still win");
    }

    /// <summary>
    /// THE CROSS-PROVIDER GUARD. `checkId` and `{type}` arrive as independent inputs, so
    /// without binding them a check stored against one provider could hand its `apiKey` to a
    /// different provider that happens to use the same schema key — one integration silently
    /// authenticating with another's credential.
    /// </summary>
    [TestMethod]
    public async Task BlankSecret_DoesNotResolveToAnotherProvidersStoredSecret()
    {
        var provider = new FakeProvider(Descriptor("ai"), (_, _) => Task.FromResult(ProbeResult.Http(FailType.NoFail, 5, 200)));
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = SeededRepo(db, new StatusCheck
        {
            Title = "stored http check",
            ProviderType = "http",   // NOT the provider being tested
            ConfigJson = """{"schemaVersion":1,"url":"https://stored.example","apiKey":"someone-elses-secret"}""",
        });
        long id = db.StatusCheckSet.Single().Id;

        await CheckProviderTestApi.RunAsync("ai", Request(id, ("url", "https://typed.example")),
            new FakeRegistry(provider), repo, CancellationToken.None);

        using var doc = JsonDocument.Parse(provider.LastConfigJson!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("apiKey", out _),
            "a stored secret must never cross provider types");
    }

    [TestMethod]
    public async Task BlankSecret_WithAnUnknownCheckId_SimplyHasNoStoredSecret()
    {
        var provider = new FakeProvider(Descriptor("ai"), (_, _) => Task.FromResult(ProbeResult.Http(FailType.NoFail, 5, 200)));

        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        await CheckProviderTestApi.RunAsync("ai", Request(999, ("url", "https://typed.example")),
            new FakeRegistry(provider), SeededRepo(db), CancellationToken.None);

        using var doc = JsonDocument.Parse(provider.LastConfigJson!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("apiKey", out _));
    }

    // ---- the message scrub ---------------------------------------------------

    /// <summary>
    /// `HttpCheckProvider` returns `ex.Message` on transport failure, and .NET transport
    /// exceptions embed the full request URL — userinfo and all. If the API forwarded
    /// `ProbeResult.Message` the operator's own credential would come straight back out of a
    /// dry run, which is the one thing this endpoint must not do.
    /// </summary>
    [TestMethod]
    public async Task ProviderExceptionText_IsNeverForwarded_EvenWhenItCarriesACredential()
    {
        const string leaky = "No such host is known (https://user:hunter2@internal.example/health)";
        var provider = new FakeProvider(Descriptor("http"),
            (_, _) => Task.FromResult(ProbeResult.Unreachable(leaky)));   // Message set, PublicMessage NOT

        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var outcome = await CheckProviderTestApi.RunAsync("http", Request(config: ("url", "https://x")),
            new FakeRegistry(provider), SeededRepo(db), CancellationToken.None);

        var message = outcome.Body!.Message ?? string.Empty;
        Assert.IsFalse(message.Contains("hunter2"), "a credential must never reach the client");
        Assert.IsFalse(message.Contains("internal.example"), "the target URL must never reach the client");
        Assert.AreEqual(leaky.Length > 0, true);
        StringAssert.Contains(message, "Could not reach the endpoint");
        Assert.AreEqual("down", outcome.Body.Outcome);
    }

    [TestMethod]
    public async Task AProviderSuppliedPublicMessage_IsForwarded()
    {
        var provider = new FakeProvider(Descriptor("ai"), (_, _) => Task.FromResult(new ProbeResult
        {
            FailType = FailType.ResponseTime,
            LatencyMs = 3200,
            Reachable = true,
            Message = "TTFT 3200ms exceeded threshold 2000ms",
            PublicMessage = "TTFT 3200ms exceeded threshold 2000ms",
        }));

        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var outcome = await CheckProviderTestApi.RunAsync("ai", Request(config: ("url", "https://x")),
            new FakeRegistry(provider), SeededRepo(db), CancellationToken.None);

        Assert.AreEqual("degraded", outcome.Body!.Outcome);
        StringAssert.Contains(outcome.Body.Message!, "exceeded threshold");
    }

    [TestMethod]
    public async Task AThrowingProvider_BecomesADownVerdict_NotAnException()
    {
        var provider = new FakeProvider(Descriptor("http"),
            (_, _) => throw new InvalidOperationException("boom at https://user:pw@host/x"));

        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var outcome = await CheckProviderTestApi.RunAsync("http", Request(config: ("url", "https://x")),
            new FakeRegistry(provider), SeededRepo(db), CancellationToken.None);

        Assert.AreEqual(CheckProviderTestApi.CheckTestStatus.Ok, outcome.Status);
        Assert.AreEqual("down", outcome.Body!.Outcome);
        Assert.IsFalse((outcome.Body.Message ?? string.Empty).Contains("pw"));
    }

    // ---- containment ---------------------------------------------------------

    /// <summary>
    /// A bare try/catch does NOT contain a provider that never observes its cancellation token —
    /// the await simply never returns. The engine uses `WaitAsync` to abandon it, and so must
    /// this endpoint, or one hostile endpoint pins an API request open indefinitely.
    /// </summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task AProviderThatIgnoresCancellation_IsAbandonedByTheBackstop()
    {
        var neverCompletes = new TaskCompletionSource<ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(
            // A 1ms probe timeout ⇒ the backstop fires ~5s later, well inside the test timeout.
            Descriptor("http", probeTimeout: TimeSpan.FromMilliseconds(1)),
            (_, _) => neverCompletes.Task);   // deliberately ignores the token

        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var outcome = await CheckProviderTestApi.RunAsync("http", Request(config: ("url", "https://x")),
            new FakeRegistry(provider), SeededRepo(db), CancellationToken.None);

        Assert.AreEqual(CheckProviderTestApi.CheckTestStatus.Ok, outcome.Status);
        Assert.AreEqual("down", outcome.Body!.Outcome, "an abandoned probe reports down, it does not hang");
    }

    // ---- no side effects -----------------------------------------------------

    /// <summary>A dry run must not move uptime numbers. The repository is the only write seam
    /// the core has, so proving it was never written is proof there is no tick, rollup or
    /// alert.</summary>
    [TestMethod]
    public async Task ADryRun_PersistsNothing()
    {
        var provider = new FakeProvider(Descriptor("http"), (_, _) => Task.FromResult(ProbeResult.Http(FailType.StatusCode, 12, 500)));
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = SeededRepo(db, new StatusCheck { Title = "seed", ProviderType = "http", ConfigJson = """{"schemaVersion":1,"url":"https://stored"}""" });
        long id = db.StatusCheckSet.Single().Id;
        int historyBefore = db.HistoricalStatusDataSet.Count();

        await CheckProviderTestApi.RunAsync("http", Request(id, ("url", "https://x")),
            new FakeRegistry(provider), repo, CancellationToken.None);

        // A dry run that came back DOWN must still leave no trace — no tick row, and the
        // check's own stored config untouched. That is what makes it safe to press repeatedly.
        Assert.AreEqual(historyBefore, db.HistoricalStatusDataSet.Count(), "a test probe records no tick");
        Assert.AreEqual("""{"schemaVersion":1,"url":"https://stored"}""", db.StatusCheckSet.Single().ConfigJson,
            "a test probe does not write the typed config back to the check");
    }

    // ---- #435 third review: a stored secret is bound to the endpoint it was saved for ----

    /// <summary>
    /// THE EXFILTRATION GUARD. A stored API key is write-only — it can never be read back
    /// through the API. But if a blank secret resolves from <c>checkId</c> while the TARGET
    /// comes from the request, an authenticated caller can name an existing check, leave the key
    /// blank, point the URL at a host they control, and have the server deliver that key there
    /// as a Bearer token. One request, nothing persisted, nothing audited.
    /// </summary>
    [TestMethod]
    public async Task BlankSecret_IsNotReused_WhenTheSubmittedTargetIsADifferentHost()
    {
        var provider = new FakeProvider(Descriptor("ai"), (_, _) => Task.FromResult(ProbeResult.Http(FailType.NoFail, 5, 200)));
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = SeededRepo(db, new StatusCheck
        {
            Title = "stored ai check",
            ProviderType = "ai",
            ConfigJson = """{"schemaVersion":1,"url":"https://trusted.example/v1","apiKey":"stored-secret"}""",
        });
        long id = db.StatusCheckSet.Single().Id;

        // Same check id, blank key, but retargeted at a host the caller chose.
        await CheckProviderTestApi.RunAsync("ai", Request(id, ("url", "https://attacker.example/v1")),
            new FakeRegistry(provider), repo, CancellationToken.None);

        using var doc = JsonDocument.Parse(provider.LastConfigJson!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("apiKey", out _),
            "a stored credential must never be sent to an endpoint it was not saved for");
    }

    /// <summary>The counterpart, so the guard is not simply "never reuse": an unchanged endpoint
    /// still keeps its key, which is what makes "leave blank to keep" usable at all.</summary>
    [TestMethod]
    public async Task BlankSecret_IsReused_WhenTheTargetIsUnchanged()
    {
        var provider = new FakeProvider(Descriptor("ai"), (_, _) => Task.FromResult(ProbeResult.Http(FailType.NoFail, 5, 200)));
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = SeededRepo(db, new StatusCheck
        {
            Title = "stored ai check",
            ProviderType = "ai",
            ConfigJson = """{"schemaVersion":1,"url":"https://trusted.example/v1","apiKey":"stored-secret"}""",
        });
        long id = db.StatusCheckSet.Single().Id;

        await CheckProviderTestApi.RunAsync("ai", Request(id, ("url", "https://trusted.example/v1")),
            new FakeRegistry(provider), repo, CancellationToken.None);

        using var doc = JsonDocument.Parse(provider.LastConfigJson!);
        Assert.AreEqual("stored-secret", doc.RootElement.GetProperty("apiKey").GetString());
    }

    /// <summary>A path or query refinement is the same endpoint — forcing a re-typed key there
    /// would make the guard hostile to normal editing without adding safety.</summary>
    [TestMethod]
    public async Task BlankSecret_IsReused_WhenOnlyThePathChanged()
    {
        var provider = new FakeProvider(Descriptor("ai"), (_, _) => Task.FromResult(ProbeResult.Http(FailType.NoFail, 5, 200)));
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = SeededRepo(db, new StatusCheck
        {
            Title = "stored ai check",
            ProviderType = "ai",
            ConfigJson = """{"schemaVersion":1,"url":"https://trusted.example/v1","apiKey":"stored-secret"}""",
        });
        long id = db.StatusCheckSet.Single().Id;

        await CheckProviderTestApi.RunAsync("ai", Request(id, ("url", "https://trusted.example/v2/other")),
            new FakeRegistry(provider), repo, CancellationToken.None);

        using var doc = JsonDocument.Parse(provider.LastConfigJson!);
        Assert.AreEqual("stored-secret", doc.RootElement.GetProperty("apiKey").GetString());
    }

    [DataTestMethod]
    [DataRow("https://trusted.example/v1", "http://trusted.example/v1", false, DisplayName = "scheme downgrade is a different endpoint")]
    [DataRow("https://trusted.example/v1", "https://trusted.example:8443/v1", false, DisplayName = "port change is a different endpoint")]
    [DataRow("https://trusted.example/v1", "https://evil.example/v1", false, DisplayName = "host change is a different endpoint")]
    [DataRow("https://trusted.example/v1", "https://TRUSTED.example/v1", true, DisplayName = "host comparison is case-insensitive")]
    [DataRow("https://trusted.example/v1", "not-a-url", false, DisplayName = "a non-URL never matches")]
    [DataRow("https://trusted.example/v1", "", false, DisplayName = "a blank target never matches")]
    public void SameEndpoint_ComparesSchemeHostAndPort(string stored, string submitted, bool expected)
        => Assert.AreEqual(expected, CheckProviderTestApi.SameEndpoint(stored, submitted));

    /// <summary>Fail closed: a provider that does not declare which field carries its target is
    /// one whose endpoint we cannot prove unchanged, so its stored secret is not reused.</summary>
    [TestMethod]
    public async Task BlankSecret_IsNotReused_WhenTheProviderDeclaresNoTargetField()
    {
        var noTarget = new ProviderDescriptor("weird", "Weird provider", "icon",
            new ConfigSchema(1, [
                new("url", "URL", ConfigFieldKind.Text, Required: true),
                new("apiKey", "API key", ConfigFieldKind.Secret, Required: false),
            ]));   // batchTargetField deliberately not declared
        var provider = new FakeProvider(noTarget, (_, _) => Task.FromResult(ProbeResult.Http(FailType.NoFail, 5, 200)));

        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = SeededRepo(db, new StatusCheck
        {
            Title = "weird", ProviderType = "weird",
            ConfigJson = """{"schemaVersion":1,"url":"https://trusted.example/v1","apiKey":"stored-secret"}""",
        });
        long id = db.StatusCheckSet.Single().Id;

        await CheckProviderTestApi.RunAsync("weird", Request(id, ("url", "https://trusted.example/v1")),
            new FakeRegistry(provider), repo, CancellationToken.None);

        using var doc = JsonDocument.Parse(provider.LastConfigJson!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("apiKey", out _),
            "a provider whose target field is unknown does not get its secret reused");
    }
}
