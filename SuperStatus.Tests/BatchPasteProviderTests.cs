using SuperStatus.Services.Providers;
using SuperStatus.Services.Providers.Ai;
using SuperStatus.Services.Providers.Heartbeat;
using SuperStatus.Services.Providers.Http;

namespace SuperStatus.Tests;

/// <summary>
/// Epic #342 (batch add) — the provider-level batch-paste capability and the pure
/// planner: which providers declare a target field, how a pasted line is parsed +
/// canonicalised, and how the planner classifies a paste (valid / duplicate / invalid,
/// dedup vs existing, derived titles). No DB — the transactional create is covered by
/// <see cref="BatchCheckCreationTests"/>.
/// </summary>
[TestClass]
public class BatchPasteProviderTests
{
    // Typed as ICheckProvider: TryParseBatchTarget is a default interface method, so it's
    // reached through the contract (which is exactly how every production call site uses it).
    private static readonly ICheckProvider Http = new HttpCheckProvider(null!);
    private static readonly ICheckProvider Ai = new AiCheckProvider(null!);
    private static readonly ICheckProvider Heartbeat = new HeartbeatCheckProvider();

    // ---- capability declaration -------------------------------------------------

    [TestMethod]
    public void Http_DeclaresUrlAsBatchTargetField()
        => Assert.AreEqual("url", Http.Descriptor.BatchTargetField);

    [TestMethod]
    public void Ai_DeclaresBaseUrlAsBatchTargetField()
        => Assert.AreEqual("baseUrl", Ai.Descriptor.BatchTargetField);

    [TestMethod]
    public void Heartbeat_DeclaresNoBatchTargetField_SoItOptsOut()
        => Assert.IsNull(Heartbeat.Descriptor.BatchTargetField);

    // ---- TryParseBatchTarget ----------------------------------------------------

    [TestMethod]
    public void Parse_BareHost_DefaultsToHttpsScheme()
    {
        Assert.IsTrue(Http.TryParseBatchTarget("web.example.com", out var value, out _));
        Assert.AreEqual("https://web.example.com", value);
    }

    [TestMethod]
    public void Parse_TrailingSlash_CanonicalisesToNoSlash()
    {
        Assert.IsTrue(Http.TryParseBatchTarget("https://host/", out var value, out _));
        Assert.AreEqual("https://host", value);
    }

    [TestMethod]
    public void Parse_PreservesPathAndQuery_TrimsTrailingPathSlash()
    {
        Assert.IsTrue(Http.TryParseBatchTarget("https://host/health/?probe=1", out var value, out _));
        Assert.AreEqual("https://host/health?probe=1", value);
    }

    [TestMethod]
    public void Parse_Junk_ReturnsFalseWithReason()
    {
        Assert.IsFalse(Http.TryParseBatchTarget("not a url", out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void Parse_NonHttpScheme_Rejected()
        => Assert.IsFalse(Http.TryParseBatchTarget("ftp://host/file", out _, out _));

    [TestMethod]
    public void Parse_HostCasing_NormalisedForDedup()
    {
        Http.TryParseBatchTarget("HTTPS://Host.EXAMPLE.com/A", out var a, out _);
        Http.TryParseBatchTarget("https://host.example.com/A", out var b, out _);
        Assert.AreEqual(a, b, "host casing must canonicalise so the two dedup as one");
    }

    [TestMethod]
    public void Parse_HeartbeatHasNoTarget_ReturnsFalse()
        => Assert.IsFalse(Heartbeat.TryParseBatchTarget("https://anything", out _, out _));

    // ---- planner ---------------------------------------------------------------

    private static readonly HashSet<string> NoExisting = new();

    [TestMethod]
    public void Plan_ClassifiesValidDuplicateInvalid_AndIgnoresBlankLines()
    {
        var lines = new[]
        {
            "https://web.example.com/health",
            "https://api.example.com/healthz",
            "",                                   // blank → ignored, not skipped
            "https://web.example.com/health",     // exact duplicate
            "not a url",                           // invalid
        };

        var plan = BatchCheckPlanner.Plan(Http, lines, NoExisting, namePrefix: null, nameTemplate: "{host}");

        Assert.AreEqual(2, plan.Valid.Count);
        Assert.AreEqual(2, plan.Skipped.Count); // one duplicate + one invalid
        CollectionAssert.AreEquivalent(
            new[] { "web.example.com", "api.example.com" },
            plan.Valid.Select(v => v.Title).ToList());
    }

    [TestMethod]
    public void Plan_DedupsByCanonicalTarget_NotRawLine()
    {
        // "https://host/" and "https://host" are the same canonical target.
        var plan = BatchCheckPlanner.Plan(Http, new[] { "https://host/", "https://host" }, NoExisting, null, "{host}");
        Assert.AreEqual(1, plan.Valid.Count);
        Assert.AreEqual(1, plan.Skipped.Count);
        StringAssert.Contains(plan.Skipped[0].Reason, "duplicate");
    }

    [TestMethod]
    public void Plan_SkipsTargetsAlreadyMonitored()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://web.example.com/health" };
        var plan = BatchCheckPlanner.Plan(Http,
            new[] { "web.example.com/health", "https://new.example.com" }, existing, null, "{host}");

        Assert.AreEqual(1, plan.Valid.Count);
        Assert.AreEqual("new.example.com", plan.Valid[0].Title);
        StringAssert.Contains(plan.Skipped.Single().Reason, "already monitors");
    }

    [TestMethod]
    public void Plan_NamePrefixAndHostPathTemplate_ApplyToTitle()
    {
        var withPath = BatchCheckPlanner.Plan(Http, new[] { "https://host/health" }, NoExisting, "prod · ", "{host}{path}");
        Assert.AreEqual("prod · host/health", withPath.Valid.Single().Title);

        var hostOnly = BatchCheckPlanner.Plan(Http, new[] { "https://host/health" }, NoExisting, null, "{host}");
        Assert.AreEqual("host", hostOnly.Valid.Single().Title);
    }

    [TestMethod]
    public void Plan_ForProviderWithoutTargetField_SkipsEverything()
    {
        // A non-batch-capable provider (heartbeat) parses nothing — every line is skipped.
        var plan = BatchCheckPlanner.Plan(Heartbeat, new[] { "https://a", "https://b" }, NoExisting, null, "{host}");
        Assert.AreEqual(0, plan.Valid.Count);
        Assert.AreEqual(2, plan.Skipped.Count);
    }
}
