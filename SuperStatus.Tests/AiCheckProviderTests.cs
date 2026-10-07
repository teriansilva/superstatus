using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Providers;
using SuperStatus.Services.Providers.Ai;
using SuperStatus.Services.Providers.Http;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Epic #271 / #317 Phase 2a: metrics foundation + the AI/LLM canary provider. Covers
/// declared-only metric validation, the AI provider's streaming classification + metric
/// emission (and non-streaming fallback), persistence of MetricsJson through the engine,
/// the recent-metrics read path, descriptor metric serialization, and that HTTP checks
/// still emit no metrics (Phase-1 parity).
/// </summary>
[TestClass]
public class AiCheckProviderTests
{
    private const string AiConfig = """{"schemaVersion":1,"baseUrl":"http://ai.test/v1","model":"m","prompt":"ping","expectContains":"pong"}""";

    // ---- MetricsValidator: only declared, finite, numeric keys survive ----------------

    private static IReadOnlyList<MetricDef> Defs() =>
        new MetricDef[] { new("ttft_ms", "TTFT", "ms", MetricKind.Gauge), new("latency_ms", "Latency", "ms", MetricKind.Gauge) };

    [TestMethod]
    public void Metrics_DropUndeclaredNonNumericNonFinite_KeepDeclared()
    {
        var json = MetricsValidator.Sanitize(
            """{"ttft_ms":12.5,"latency_ms":40,"hacker":99,"bad":"x","inf":1e400}""", Defs());
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual(12.5, root.GetProperty("ttft_ms").GetDouble());
        Assert.AreEqual(40, root.GetProperty("latency_ms").GetDouble());
        Assert.IsFalse(root.TryGetProperty("hacker", out _), "undeclared key dropped");
        Assert.IsFalse(root.TryGetProperty("bad", out _), "non-numeric dropped");
        Assert.IsFalse(root.TryGetProperty("inf", out _), "non-finite dropped");
    }

    [TestMethod]
    public void Metrics_NoneDeclared_ReturnsNull()
        => Assert.IsNull(MetricsValidator.Sanitize("""{"x":1}""", Array.Empty<MetricDef>()));

    [TestMethod]
    public void Metrics_NothingValid_ReturnsNull()
        => Assert.IsNull(MetricsValidator.Sanitize("""{"hacker":1}""", Defs()));

    // ---- AiCheckProvider: streaming classification + metrics --------------------------

    [TestMethod]
    public async Task Ai_HealthyStream_NoFail_EmitsDeclaredMetrics()
    {
        var provider = new AiCheckProvider(StubFactory(Sse("pong", tokens: 5)));
        var r = await provider.ProbeAsync(Ctx(AiConfig));

        Assert.AreEqual(FailType.NoFail, r.FailType);
        Assert.IsTrue(r.Reachable);
        using var m = JsonDocument.Parse(r.MetricsJson!);
        var root = m.RootElement;
        foreach (var k in new[] { "ttft_ms", "tokens_per_sec", "latency_ms", "completion_tokens" })
            Assert.IsTrue(root.TryGetProperty(k, out _), $"emits {k}");
        Assert.AreEqual(5, root.GetProperty("completion_tokens").GetDouble());
    }

    [TestMethod]
    public async Task Ai_ContentMissing_StatusCode_Down()
    {
        var provider = new AiCheckProvider(StubFactory(Sse("nope", tokens: 3)));
        var r = await provider.ProbeAsync(Ctx(AiConfig));
        Assert.AreEqual(FailType.StatusCode, r.FailType, "missing expected content ⇒ down");
        Assert.IsTrue(r.Reachable);
    }

    [TestMethod]
    public async Task Ai_HttpError_Unreachable()
    {
        var provider = new AiCheckProvider(StubFactory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var r = await provider.ProbeAsync(Ctx(AiConfig));
        Assert.AreEqual(FailType.Unreachable, r.FailType);
        Assert.IsFalse(r.Reachable);
    }

    [TestMethod]
    public async Task Ai_BelowMinThroughput_ResponseTime_Degraded()
    {
        // minTokensPerSec absurdly high → measured throughput is below it → degraded.
        var cfg = """{"schemaVersion":1,"baseUrl":"http://ai.test/v1","model":"m","prompt":"ping","expectContains":"pong","minTokensPerSec":1000000000}""";
        var provider = new AiCheckProvider(StubFactory(Sse("pong", tokens: 5)));
        var r = await provider.ProbeAsync(Ctx(cfg));
        Assert.AreEqual(FailType.ResponseTime, r.FailType, "throughput below the floor ⇒ degraded");
    }

    [TestMethod]
    public async Task Ai_NonStreamingResponse_FallbackParse_NoFail()
    {
        // A provider that ignores stream:true and returns a plain completion.
        var body = """{"choices":[{"message":{"content":"pong!"}}],"usage":{"completion_tokens":2}}""";
        var provider = new AiCheckProvider(StubFactory(_ => Ok(body)));
        var r = await provider.ProbeAsync(Ctx(AiConfig));
        Assert.AreEqual(FailType.NoFail, r.FailType, "non-streaming body is parsed via the fallback");
    }

    // ---- engine: MetricsJson persisted for AI, null for HTTP (parity) -----------------

    [TestMethod]
    public async Task Execute_Ai_PersistsDeclaredMetrics()
    {
        var (db, conn) = Relational(); using var dbScope = db; using var connScope = conn;
        var check = PersistCheck(db, providerType: "ai", configJson: AiConfig);
        var factory = StubFactory(Sse("pong", tokens: 5));
        var registry = new CheckProviderRegistry(new ICheckProvider[] { new HttpCheckProvider(factory), new AiCheckProvider(factory) });
        var svc = Service(db, factory, registry);

        var data = await svc.ExecuteStatusCheck(check);

        Assert.AreEqual(FailType.NoFail, data.FailType);
        Assert.IsNotNull(data.MetricsJson, "an AI tick carries metrics");
        using var m = JsonDocument.Parse(data.MetricsJson!);
        Assert.IsTrue(m.RootElement.TryGetProperty("ttft_ms", out _));
    }

    [TestMethod]
    public async Task Execute_Http_NoMetrics_ParityPreserved()
    {
        var (db, conn) = Relational(); using var dbScope = db; using var connScope = conn;
        var check = PersistCheck(db, providerType: "http", configJson: """{"schemaVersion":1,"url":"http://x","expectedStatusCode":200}""");
        var factory = StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = new CheckProviderRegistry(new ICheckProvider[] { new HttpCheckProvider(factory), new AiCheckProvider(factory) });
        var svc = Service(db, factory, registry);

        var data = await svc.ExecuteStatusCheck(check);

        Assert.AreEqual(FailType.NoFail, data.FailType);
        Assert.IsNull(data.MetricsJson, "http checks emit no metrics — Phase-1 parity");
    }

    // ---- recent-metrics read path -----------------------------------------------------

    [TestMethod]
    public async Task GetRecentMetrics_ReturnsDefsAndSamples()
    {
        var (db, conn) = Relational(); using var dbScope = db; using var connScope = conn;
        var check = PersistCheck(db, providerType: "ai", configJson: AiConfig);
        var factory = StubFactory(Sse("pong", tokens: 5));
        var registry = new CheckProviderRegistry(new ICheckProvider[] { new HttpCheckProvider(factory), new AiCheckProvider(factory) });
        var svc = Service(db, factory, registry);

        var data = await svc.ExecuteStatusCheck(check);
        await svc.SaveStatusCheckResult(data);

        var metrics = await svc.GetRecentMetricsAsync(check.Id, 50);
        Assert.IsNotNull(metrics);
        Assert.AreEqual("ai", metrics!.ProviderType);
        Assert.IsTrue(metrics.MetricDefs.Any(d => d.Key == "ttft_ms"), "declared defs surfaced");
        Assert.AreEqual(1, metrics.Samples.Count);
        Assert.IsTrue(metrics.Samples[0].Values.ContainsKey("latency_ms"));
    }

    [TestMethod]
    public async Task GetRecentMetrics_UnknownCheck_Null()
    {
        var (db, conn) = Relational(); using var dbScope = db; using var connScope = conn;
        var factory = StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = Service(db, factory, new CheckProviderRegistry(new ICheckProvider[] { new HttpCheckProvider(factory) }));
        Assert.IsNull(await svc.GetRecentMetricsAsync(999, 10));
    }

    // ---- dashboard state derives from FailType (correct for AI) -----------------------

    [TestMethod]
    public void MostRecentState_AiDownByContent_IsDown()
    {
        // An AI check that's reachable but failed the content assertion: HttpStatusCode=0,
        // ExpectedStatusCode=0 — the old HTTP recompute would read "up"; FailType says down.
        var check = new StatusCheck { Id = 1, ProviderType = "ai", ExpectedStatusCode = 0 };
        var tick = new HistoricalStatusData { CheckFailed = false, HttpStatusCode = 0, FailType = FailType.StatusCode };
        Assert.AreEqual("down", StatusCheckService.MostRecentState(check, tick));
    }

    // ---- descriptor metric serialization (Phase-2c consumers) -------------------------

    [TestMethod]
    public void Descriptor_AiProvider_DeclaresMetrics()
    {
        var dto = SuperStatus.ApiService.CheckProviderApi.ToViewModel(new AiCheckProvider(StubFactory(_ => Ok("{}"))).Descriptor);
        Assert.AreEqual("ai", dto.TypeId);
        CollectionAssert.AreEquivalent(
            new[] { "ttft_ms", "tokens_per_sec", "latency_ms", "completion_tokens" },
            dto.Metrics.Select(m => m.Key).ToArray());
        Assert.IsTrue(dto.Fields.Any(f => f.Key == "apiKey" && f.Kind == "secret"), "apiKey is a secret field");
    }

    // ---- helpers ----------------------------------------------------------------------

    private static ProbeContext Ctx(string configJson) => new(1, "ai-canary", configJson, TimeSpan.FromSeconds(30));

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Func<HttpRequestMessage, HttpResponseMessage> Sse(string content, int tokens) =>
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SseText(content, tokens), Encoding.UTF8, "text/event-stream") };

    private static string SseText(string content, int tokens)
    {
        int mid = Math.Max(1, content.Length / 2);
        string a = content[..mid], b = content[mid..];
        return
            "data: {\"choices\":[{\"delta\":{\"content\":\"" + a + "\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"" + b + "\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"completion_tokens\":" + tokens + "}}\n\n" +
            "data: [DONE]\n\n";
    }

    private static (SuperStatusDb, SqliteConnection) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck PersistCheck(SuperStatusDb db, string providerType, string configJson)
    {
        var check = new StatusCheck
        {
            Title = "probe",
            StatusCheckUrl = "http://probe.test/health",
            ExpectedStatusCode = 200,
            ProviderType = providerType,
            ConfigJson = configJson,
            Enabled = true,
            ServiceLogoUrl = string.Empty,
            Created = DateTime.UtcNow,
        };
        SlaTestUtil.Attach(check, slowThresholdMs: 60_000);
        db.StatusCheckSet.Add(check);
        db.SaveChanges();
        return check;
    }

    private static StatusCheckService Service(SuperStatusDb db, IHttpClientFactory factory, ICheckProviderRegistry registry) =>
        new(new StatusCheckRepository(db),
            new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db),
            new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            factory,
            NullLogger<StatusCheckService>.Instance,
            statusCheckLinkRepository: new StatusCheckLinkRepository(db),
            checkProviderRegistry: registry);

    private static IHttpClientFactory StubFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) => new FuncFactory(responder);

    private sealed class FuncFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FuncHandler(responder));

        private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return f(request);
            }
        }
    }
}
