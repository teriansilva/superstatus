using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Providers;
using SuperStatus.Services.Providers.Http;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Epic #271 / #312 Phase 1: the pluggable check-provider seam. Covers the contract
/// (config schema validation, the HTTP provider's protocol classification), the shared
/// resolve-or-disable gate, the behavior-preserving adapter (incl. the SLA latency SLO
/// and run-now-style execution), hard probe containment, and the secret write rule.
/// HTTP byte-for-byte parity is additionally guarded by <see cref="StatusCheckHttpClientTests"/>,
/// which exercises ExecuteStatusCheck through this same seam via the http fallback.
/// </summary>
[TestClass]
public class CheckProviderTests
{
    // ---- ConfigSchema validation ----------------------------------------------------

    private static ConfigSchema HttpSchema() => new HttpCheckProvider(null!).Descriptor.ConfigSchema;

    [TestMethod]
    public void Validate_ValidHttpConfig_ReturnsNull()
        => Assert.IsNull(HttpSchema().Validate("""{"schemaVersion":1,"url":"https://x/health","expectedStatusCode":200}"""));

    [TestMethod]
    public void Validate_MalformedJson_ReturnsError()
        => Assert.IsNotNull(HttpSchema().Validate("{ not json"));

    [TestMethod]
    public void Validate_MissingRequiredUrl_ReturnsError()
    {
        var err = HttpSchema().Validate("""{"schemaVersion":1,"expectedStatusCode":200}""");
        StringAssert.Contains(err, "URL");
    }

    [TestMethod]
    public void Validate_IncompatibleSchemaVersion_ReturnsError()
    {
        var err = HttpSchema().Validate("""{"schemaVersion":999,"url":"https://x","expectedStatusCode":200}""");
        StringAssert.Contains(err, "v999");
    }

    [TestMethod]
    public void Validate_UnsupportedSelectValue_ReturnsError()
    {
        var schema = new ConfigSchema(1, new ConfigField[]
        {
            new("method", "Method", ConfigFieldKind.Select, Required: true,
                Options: new ConfigSelectOption[] { new("GET", "GET"), new("HEAD", "HEAD") }),
        });
        Assert.IsNotNull(schema.Validate("""{"method":"DELETE"}"""), "a value outside the option list is rejected");
        Assert.IsNull(schema.Validate("""{"method":"HEAD"}"""));
    }

    [TestMethod]
    public void Validate_ArbitraryHttpStatusCode_Accepted()
    {
        // #312 parity: expectedStatusCode is a number, so a check expecting any code
        // (e.g. 503/418) stays valid — it must NOT be disabled on migration.
        Assert.IsNull(HttpSchema().Validate("""{"schemaVersion":1,"url":"https://x","expectedStatusCode":503}"""));
        Assert.IsNull(HttpSchema().Validate("""{"schemaVersion":1,"url":"https://x","expectedStatusCode":418}"""));
    }

    // ---- HttpCheckProvider: protocol-level classification ---------------------------

    [TestMethod]
    public async Task HttpProvider_MatchingStatus_NoFail()
    {
        var provider = new HttpCheckProvider(StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await provider.ProbeAsync(Ctx("""{"url":"http://x","expectedStatusCode":200}"""));
        Assert.AreEqual(FailType.NoFail, result.FailType);
        Assert.AreEqual(200, result.HttpStatusCode);
        Assert.IsTrue(result.Reachable);
    }

    [TestMethod]
    public async Task HttpProvider_WrongStatus_StatusCode()
    {
        var provider = new HttpCheckProvider(StubFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var result = await provider.ProbeAsync(Ctx("""{"url":"http://x","expectedStatusCode":200}"""));
        Assert.AreEqual(FailType.StatusCode, result.FailType);
        Assert.AreEqual(500, result.HttpStatusCode);
        Assert.IsTrue(result.Reachable);
    }

    [TestMethod]
    public async Task HttpProvider_TransportFailure_Unreachable()
    {
        var provider = new HttpCheckProvider(StubFactory(_ => throw new TaskCanceledException("boom")));
        var result = await provider.ProbeAsync(Ctx("""{"url":"http://x","expectedStatusCode":200}"""));
        Assert.AreEqual(FailType.Unreachable, result.FailType);
        Assert.AreEqual(0, result.HttpStatusCode);
        Assert.AreEqual(0, result.LatencyMs);
        Assert.IsFalse(result.Reachable);
    }

    // ---- ResolveProbe: the shared resolve-or-disable gate ---------------------------

    [TestMethod]
    public void Resolve_UnknownProviderType_Disabled()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db);
        check.ProviderType = "tcp";
        var svc = ServiceWithRegistry(db);

        var resolution = svc.ResolveProbe(check);

        Assert.IsTrue(resolution.IsDisabled);
        StringAssert.Contains(resolution.DisableReason, "tcp");
    }

    [TestMethod]
    public void Resolve_MalformedConfig_Disabled()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db);
        check.ConfigJson = "{ not json";
        var svc = ServiceWithRegistry(db);

        Assert.IsTrue(svc.ResolveProbe(check).IsDisabled);
    }

    [TestMethod]
    public void Resolve_IncompatibleVersion_Disabled()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db);
        check.ConfigJson = """{"schemaVersion":999,"url":"http://x","expectedStatusCode":200}""";
        var svc = ServiceWithRegistry(db);

        Assert.IsTrue(svc.ResolveProbe(check).IsDisabled);
    }

    [TestMethod]
    public void Resolve_ValidHttp_NotDisabled()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db);
        check.ConfigJson = """{"schemaVersion":1,"url":"http://x","expectedStatusCode":200}""";
        var svc = ServiceWithRegistry(db);

        Assert.IsFalse(svc.ResolveProbe(check).IsDisabled);
    }

    [TestMethod]
    public void Resolve_EmptyConfig_FallsBackToColumns()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db);
        check.ConfigJson = null;   // pre-migration / ad-hoc row
        var svc = ServiceWithRegistry(db);

        var resolution = svc.ResolveProbe(check);

        Assert.IsFalse(resolution.IsDisabled);
        StringAssert.Contains(resolution.EffectiveConfigJson, "url");
    }

    // ---- ExecuteStatusCheck: adapter + SLA SLO + containment ------------------------

    [TestMethod]
    public async Task Execute_ViaRegistry_HealthyHttp_NoFail_NullMetrics()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db, slowThresholdMs: 60_000);
        var svc = ServiceWithRegistry(db, _ => new HttpResponseMessage(HttpStatusCode.OK));

        var data = await svc.ExecuteStatusCheck(check);

        Assert.AreEqual(FailType.NoFail, data.FailType);
        Assert.AreEqual(200, data.HttpStatusCode);
        Assert.IsNull(data.MetricsJson);   // Phase 1 emits no metrics
    }

    [TestMethod]
    public async Task Execute_HealthyButSlow_BecomesResponseTime()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        // Slow threshold of 1ms; the stub delays ~25ms so latency exceeds it.
        var check = PersistHttpCheck(db, slowThresholdMs: 1);
        var svc = ServiceWithRegistry(db, _ => new HttpResponseMessage(HttpStatusCode.OK), delayMs: 25);

        var data = await svc.ExecuteStatusCheck(check);

        Assert.AreEqual(FailType.ResponseTime, data.FailType, "a healthy response slower than the SLA slow threshold is degraded.");
    }

    [TestMethod]
    public void Resolve_LegacyArbitraryStatusCode_NotDisabled()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db);
        // A pre-existing check expecting a non-"common" code, backfilled like the migration does.
        check.ExpectedStatusCode = 503;
        check.ConfigJson = """{"schemaVersion":1,"url":"http://x","expectedStatusCode":503}""";
        var svc = ServiceWithRegistry(db);

        Assert.IsFalse(svc.ResolveProbe(check).IsDisabled, "a check expecting an arbitrary status code keeps probing, not disabled.");
    }

    [TestMethod]
    public async Task Execute_ProviderIgnoringCancellation_ContainedByBackstop()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db);
        check.ProviderType = HangingProvider.Id;
        var registry = new CheckProviderRegistry(new ICheckProvider[]
        {
            new HttpCheckProvider(StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new HangingProvider(),
        });
        var svc = Service(db, StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)), registry);
        svc.ProbeBackstop = TimeSpan.FromMilliseconds(150);   // drive the backstop short so the test is fast

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = await svc.ExecuteStatusCheck(check);
        sw.Stop();

        Assert.AreEqual(FailType.Unreachable, data.FailType, "a provider that ignores cancellation is abandoned at the backstop, not awaited forever.");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(3), "containment must not wait for the hung provider.");
    }

    [TestMethod]
    public void ProviderConfig_BoolAndNumber_WriteRealJsonTypes_AndValidate()
    {
        var schema = new ConfigSchema(1, new ConfigField[]
        {
            new("followRedirects", "Follow redirects", ConfigFieldKind.Bool),
            new("port", "Port", ConfigFieldKind.Number),
        });
        var json = ProviderConfigWriter.Build(schema,
            new Dictionary<string, string> { ["followRedirects"] = "true", ["port"] = "5432" }, existingJson: null);

        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual(JsonValueKind.True, doc.RootElement.GetProperty("followRedirects").ValueKind, "bool stored as a real JSON bool, not a string");
        Assert.AreEqual(JsonValueKind.Number, doc.RootElement.GetProperty("port").ValueKind, "number stored as a real JSON number");
        Assert.IsNull(schema.Validate(json), "the written config validates against its own schema (would self-disable otherwise).");
    }

    [TestMethod]
    public async Task Execute_ThrowingProvider_ContainedAsUnreachable()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = PersistHttpCheck(db);
        check.ProviderType = ThrowingProvider.Id;
        var registry = new CheckProviderRegistry(new ICheckProvider[]
        {
            new HttpCheckProvider(StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new ThrowingProvider(),
        });
        var svc = Service(db, StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)), registry);

        var data = await svc.ExecuteStatusCheck(check);

        Assert.AreEqual(FailType.Unreachable, data.FailType, "a provider that throws is contained, not propagated.");
        Assert.IsTrue(data.CheckFailed);
    }

    // ---- ProviderConfigWriter: the secret rule --------------------------------------

    private static ConfigSchema SecretSchema() => new(1, new ConfigField[]
    {
        new("url", "URL", ConfigFieldKind.Text, Required: true),
        new("apiKey", "API key", ConfigFieldKind.Secret, Required: true),
    });

    [TestMethod]
    public void SecretWrite_BlankSecret_PreservesStored()
    {
        var existing = """{"schemaVersion":1,"url":"a","apiKey":"STORED"}""";
        var incoming = new Dictionary<string, string> { ["url"] = "b", ["apiKey"] = "" };

        var json = ProviderConfigWriter.Build(SecretSchema(), incoming, existing);

        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("b", doc.RootElement.GetProperty("url").GetString());
        Assert.AreEqual("STORED", doc.RootElement.GetProperty("apiKey").GetString());
    }

    [TestMethod]
    public void SecretWrite_NonBlankSecret_Replaces()
    {
        var existing = """{"url":"a","apiKey":"STORED"}""";
        var incoming = new Dictionary<string, string> { ["url"] = "a", ["apiKey"] = "NEW" };

        var json = ProviderConfigWriter.Build(SecretSchema(), incoming, existing);

        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("NEW", doc.RootElement.GetProperty("apiKey").GetString());
        Assert.AreEqual(1, doc.RootElement.GetProperty(ConfigSchema.VersionKey).GetInt32());
    }

    [TestMethod]
    public void SecretWrite_NoStoredSecret_BlankOmitsKey()
    {
        var incoming = new Dictionary<string, string> { ["url"] = "a", ["apiKey"] = "" };

        var json = ProviderConfigWriter.Build(SecretSchema(), incoming, existingJson: null);

        using var doc = JsonDocument.Parse(json);
        Assert.IsFalse(doc.RootElement.TryGetProperty("apiKey", out _), "no stored secret + blank input ⇒ key omitted, never an empty credential.");
    }

    // ---- save round-trip: ProviderType + ConfigJson populated and reversible --------

    [TestMethod]
    public async Task AddOrUpdate_Http_PersistsProviderTypeAndConfig_RoundTrips()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithRegistry(db);

        var vm = new StatusCheckViewModelBase
        {
            Title = "Public API",
            ProviderType = "http",
            IntervalSeconds = 30,
            Enabled = true,
            ServiceLogoUrl = string.Empty,
        };
        vm.ProviderConfig["url"] = "https://api.example.com/health";
        vm.ProviderConfig["expectedStatusCode"] = "201";

        var saved = await svc.AddOrUpdateStatusCheck(vm);

        Assert.AreEqual("http", saved.ProviderType);
        Assert.AreEqual("https://api.example.com/health", saved.StatusCheckUrl);
        Assert.AreEqual(201, saved.ExpectedStatusCode);
        StringAssert.Contains(saved.ConfigJson, "https://api.example.com/health");

        // Round-trips through the read view model back to the same config values.
        var readVm = new StatusCheckViewModel(saved, null);
        Assert.AreEqual("http", readVm.ProviderType);
        Assert.AreEqual("https://api.example.com/health", readVm.ProviderConfig["url"]);
        Assert.AreEqual("201", readVm.ProviderConfig["expectedStatusCode"]);
    }

    // ---- secret never leaks into the public status contract -------------------------

    [TestMethod]
    public void PublicStatusServiceDto_HasNoConfigOrSecretField()
    {
        var leak = typeof(SuperStatus.ApiService.PublicStatusServiceDto)
            .GetProperties()
            .Select(p => p.Name)
            .FirstOrDefault(n => n.Contains("Config", StringComparison.OrdinalIgnoreCase)
                               || n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(leak, $"the public status DTO must never expose provider config/secrets (found '{leak}').");
    }

    // ---- helpers --------------------------------------------------------------------

    private static ProbeContext Ctx(string configJson) => new(1, "probe", configJson, TimeSpan.FromSeconds(10));

    private static (SuperStatusDb, SqliteConnection) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck PersistHttpCheck(SuperStatusDb db, long slowThresholdMs = 60_000)
    {
        var check = new StatusCheck
        {
            Title = "probe",
            StatusCheckUrl = "http://probe.test/health",
            ExpectedStatusCode = 200,
            ProviderType = "http",
            Enabled = true,
            ServiceLogoUrl = string.Empty,
            Created = DateTime.UtcNow,
        };
        SlaTestUtil.Attach(check, slowThresholdMs);
        db.StatusCheckSet.Add(check);
        db.SaveChanges();
        return check;
    }

    private static StatusCheckService Service(SuperStatusDb db, IHttpClientFactory factory, ICheckProviderRegistry? registry) =>
        new(new StatusCheckRepository(db),
            new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db),
            new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            factory,
            NullLogger<StatusCheckService>.Instance,
            statusCheckLinkRepository: new StatusCheckLinkRepository(db),
            checkProviderRegistry: registry);

    private static StatusCheckService ServiceWithRegistry(
        SuperStatusDb db,
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        int delayMs = 0)
    {
        var factory = StubFactory(responder ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)), delayMs);
        var registry = new CheckProviderRegistry(new ICheckProvider[] { new HttpCheckProvider(factory) });
        return Service(db, factory, registry);
    }

    private static IHttpClientFactory StubFactory(Func<HttpRequestMessage, HttpResponseMessage> responder, int delayMs = 0)
        => new FuncFactory(responder, delayMs);

    private sealed class FuncFactory(Func<HttpRequestMessage, HttpResponseMessage> responder, int delayMs) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new FuncHandler(responder, delayMs)) { Timeout = TimeSpan.FromSeconds(10) };

        private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> f, int delayMs) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
                else await Task.Yield();
                return f(request);
            }
        }
    }

    /// <summary>A provider that always throws — to prove the engine contains it.</summary>
    private sealed class ThrowingProvider : ICheckProvider
    {
        public const string Id = "boom";

        public ProviderDescriptor Descriptor { get; } = new(
            Id, "Boom", "bug", new ConfigSchema(1, Array.Empty<ConfigField>()));

        public Task<ProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider blew up");
    }

    /// <summary>A provider that hangs and ignores cancellation — to prove the WaitAsync
    /// backstop abandons it rather than pinning the scope forever.</summary>
    private sealed class HangingProvider : ICheckProvider
    {
        public const string Id = "hang";

        public ProviderDescriptor Descriptor { get; } = new(
            Id, "Hang", "", new ConfigSchema(1, Array.Empty<ConfigField>()));

        public async Task<ProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken = default)
        {
            // Deliberately ignores the supplied token (CancellationToken.None).
            await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
            return new ProbeResult();
        }
    }
}
