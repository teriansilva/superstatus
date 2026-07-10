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
using SuperStatus.Services.Providers.Heartbeat;
using SuperStatus.Services.Providers.Http;
using SuperStatus.Services.Services;
using SuperStatus.Services.Plugins;

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

    // ---- #320 Phase 2b: agent-heartbeat (push) provider -----------------------------

    private static string HbConfig(int intervalSeconds = 3600, int graceSeconds = 300)
        => $"{{\"schemaVersion\":1,\"intervalSeconds\":{intervalSeconds},\"graceSeconds\":{graceSeconds}}}";

    private static ProbeContext HbCtx(string configJson, DateTime? lastSignalUtc)
        => new(1, "heartbeat", configJson, TimeSpan.FromSeconds(10), lastSignalUtc);

    [TestMethod]
    public async Task Heartbeat_FreshPing_Up_EmitsSecondsSinceMetric()
    {
        var probe = await new HeartbeatCheckProvider()
            .ProbeAsync(HbCtx(HbConfig(), DateTime.UtcNow.AddSeconds(-10)));

        Assert.AreEqual(FailType.NoFail, probe.FailType, "a ping within interval+grace is up.");
        Assert.IsTrue(probe.Reachable);
        Assert.IsNotNull(probe.MetricsJson, "a finite age emits the seconds_since_heartbeat metric.");
        using var doc = JsonDocument.Parse(probe.MetricsJson!);
        double age = doc.RootElement.GetProperty(HeartbeatCheckProvider.MetricSecondsSinceHeartbeat).GetDouble();
        Assert.IsTrue(age is >= 9 and <= 60, $"emitted age {age}s reflects the last ping.");
    }

    [TestMethod]
    public async Task Heartbeat_StalePing_Down_WithMessage()
    {
        var probe = await new HeartbeatCheckProvider()
            .ProbeAsync(HbCtx(HbConfig(intervalSeconds: 3600, graceSeconds: 300), DateTime.UtcNow.AddHours(-2)));

        Assert.AreEqual(FailType.Unreachable, probe.FailType, "overdue past interval+grace is down.");
        Assert.IsFalse(probe.Reachable);
        StringAssert.Contains(probe.Message, "no heartbeat for", "a stale check explains how overdue it is.");
        Assert.IsNotNull(probe.MetricsJson, "even a down (but previously-pinged) check still reports its age.");
    }

    [TestMethod]
    public async Task Heartbeat_NeverPinged_Down_NoMetric()
    {
        var probe = await new HeartbeatCheckProvider()
            .ProbeAsync(HbCtx(HbConfig(), lastSignalUtc: null));

        Assert.AreEqual(FailType.Unreachable, probe.FailType, "a check that never received a ping is down.");
        Assert.IsFalse(probe.Reachable);
        Assert.IsNull(probe.MetricsJson, "a 'never pinged' age isn't a number — no metric emitted.");
        StringAssert.Contains(probe.Message, "no heartbeat received yet");
    }

    [TestMethod]
    public void Heartbeat_DescriptorDeclares_SecondsSinceMetric()
    {
        var defs = new HeartbeatCheckProvider().Descriptor.MetricDefs;
        Assert.IsTrue(defs.Any(d => d.Key == HeartbeatCheckProvider.MetricSecondsSinceHeartbeat),
            "the declared MetricDef is what MetricsValidator.Sanitize keeps — an undeclared metric would be dropped.");
    }

    [TestMethod]
    public async Task AddOrUpdate_Heartbeat_GeneratesToken_StampsLastHeartbeat_NoUrlNeeded()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithHeartbeat(db);

        var vm = new StatusCheckViewModelBase
        {
            Title = "Nightly backup job",
            ProviderType = HeartbeatCheckProvider.TypeId,
            IntervalSeconds = 60,
            Enabled = true,
            ServiceLogoUrl = string.Empty,
            StatusCheckUrl = string.Empty,   // push provider: no URL
        };
        vm.ProviderConfig["intervalSeconds"] = "3600";
        vm.ProviderConfig["graceSeconds"] = "300";

        var saved = await svc.AddOrUpdateStatusCheck(vm);

        Assert.AreEqual(HeartbeatCheckProvider.TypeId, saved.ProviderType);
        Assert.IsFalse(string.IsNullOrEmpty(saved.HeartbeatToken), "a heartbeat check gets a token on create.");
        Assert.AreEqual(32, saved.HeartbeatToken!.Length, "128-bit token as lowercase hex.");
        Assert.IsNotNull(saved.LastHeartbeatUtc);
        Assert.IsTrue((DateTime.UtcNow - saved.LastHeartbeatUtc!.Value) < TimeSpan.FromMinutes(1),
            "LastHeartbeatUtc is stamped to now so a fresh check has interval+grace before it can flip down.");
    }

    [TestMethod]
    public async Task RecordHeartbeat_KnownToken_AdvancesTimestamp_ReturnsTrue()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithHeartbeat(db);
        var check = PersistHeartbeatCheck(db, DateTime.UtcNow.AddHours(-1));

        bool ok = await svc.RecordHeartbeatAsync(check.HeartbeatToken!);

        Assert.IsTrue(ok, "a known token records a ping.");
        var reloaded = await db.StatusCheckSet.AsNoTracking().FirstAsync(c => c.Id == check.Id);
        Assert.IsTrue((DateTime.UtcNow - reloaded.LastHeartbeatUtc!.Value) < TimeSpan.FromMinutes(1),
            "the ping advanced LastHeartbeatUtc to now.");
    }

    [TestMethod]
    public async Task RecordHeartbeat_UnknownToken_ReturnsFalse()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithHeartbeat(db);
        PersistHeartbeatCheck(db, DateTime.UtcNow);

        Assert.IsFalse(await svc.RecordHeartbeatAsync("deadbeefdeadbeefdeadbeefdeadbeef"),
            "an unknown token matches no row — the endpoint answers a flat 404.");
        Assert.IsFalse(await svc.RecordHeartbeatAsync(""), "empty token is a no-op false.");
    }

    [TestMethod]
    public async Task GetHeartbeatToken_HeartbeatCheck_ReturnsToken_OtherProvider_ReturnsNull()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithHeartbeat(db);
        var hb = PersistHeartbeatCheck(db, DateTime.UtcNow);
        var http = PersistHttpCheck(db);

        Assert.AreEqual(hb.HeartbeatToken, await svc.GetHeartbeatTokenAsync(hb.Id));
        Assert.IsNull(await svc.GetHeartbeatTokenAsync(http.Id), "a non-heartbeat check exposes no token.");
        Assert.IsNull(await svc.GetHeartbeatTokenAsync(999_999), "an unknown check exposes no token.");
    }

    [TestMethod]
    public async Task RegenerateHeartbeatToken_RotatesAndInvalidatesOldUrl()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithHeartbeat(db);
        var check = PersistHeartbeatCheck(db, DateTime.UtcNow);
        string oldToken = check.HeartbeatToken!;

        string? newToken = await svc.RegenerateHeartbeatTokenAsync(check.Id);

        Assert.IsFalse(string.IsNullOrEmpty(newToken));
        Assert.AreNotEqual(oldToken, newToken, "the token rotates.");
        Assert.IsFalse(await svc.RecordHeartbeatAsync(oldToken), "the OLD ping URL stops working immediately.");
        Assert.IsTrue(await svc.RecordHeartbeatAsync(newToken!), "the new URL works.");
    }

    [TestMethod]
    public async Task Execute_HeartbeatFreshOnCreate_Up_StaleAfter_Down()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithHeartbeat(db);

        // Fresh: last ping = now ⇒ ProbeContext.LastSignalUtc flows through and classifies up.
        var fresh = PersistHeartbeatCheck(db, DateTime.UtcNow, intervalSeconds: 3600, graceSeconds: 300);
        var up = await svc.ExecuteStatusCheck(fresh);
        Assert.AreEqual(FailType.NoFail, up.FailType, "a just-pinged heartbeat check is up end-to-end.");

        // Stale: last ping = 2h ago ⇒ overdue ⇒ down.
        var stale = PersistHeartbeatCheck(db, DateTime.UtcNow.AddHours(-2), intervalSeconds: 3600, graceSeconds: 300);
        var down = await svc.ExecuteStatusCheck(stale);
        Assert.AreEqual(FailType.Unreachable, down.FailType, "an overdue heartbeat check is down end-to-end.");
    }

    [TestMethod]
    public async Task Execute_NonHeartbeatProvider_ReceivesNullLastSignal()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var capturing = new CapturingProvider();
        var registry = new CheckProviderRegistry(new ICheckProvider[]
        {
            new HttpCheckProvider(StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            capturing,
        });
        var svc = Service(db, StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)), registry);

        var check = PersistHttpCheck(db);
        check.ProviderType = CapturingProvider.Id;   // a pull provider — LastHeartbeatUtc stays null
        db.SaveChanges();

        await svc.ExecuteStatusCheck(check);

        Assert.IsTrue(capturing.Captured, "the provider ran.");
        Assert.IsNull(capturing.LastSignalUtc, "a non-heartbeat (pull) check passes a null LastSignalUtc.");
    }

    [TestMethod]
    public async Task RecordHeartbeat_TokenOnNonHeartbeatRow_DoesNotRecord()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithHeartbeat(db);

        // Defence-in-depth: a row still carrying a heartbeat token but no longer a heartbeat
        // provider must NOT be pingable — the record path filters on the heartbeat type.
        var check = PersistHeartbeatCheck(db, DateTime.UtcNow);
        string token = check.HeartbeatToken!;
        check.ProviderType = "http";
        db.SaveChanges();

        Assert.IsFalse(await svc.RecordHeartbeatAsync(token), "a stale token on a non-heartbeat row records nothing.");
    }

    [TestMethod]
    public async Task Convert_HeartbeatAwayAndBack_RevokesOldToken_MintsFreshOnReturn()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var svc = ServiceWithHeartbeat(db);

        // 1) Create a heartbeat check → it has a working ping token.
        var hbVm = new StatusCheckViewModelBase
        {
            Title = "Worker", ProviderType = HeartbeatCheckProvider.TypeId,
            IntervalSeconds = 60, Enabled = true, ServiceLogoUrl = string.Empty, StatusCheckUrl = string.Empty,
        };
        hbVm.ProviderConfig["intervalSeconds"] = "3600";
        hbVm.ProviderConfig["graceSeconds"] = "300";
        var hb = await svc.AddOrUpdateStatusCheck(hbVm);
        string oldToken = hb.HeartbeatToken!;
        Assert.IsTrue(await svc.RecordHeartbeatAsync(oldToken), "the URL records while it's a heartbeat check.");

        // 2) Convert it to http → the ping credential is revoked, the old URL dies.
        var toHttp = new StatusCheckViewModelBase
        {
            Id = hb.Id, Title = hb.Title, ProviderType = "http",
            IntervalSeconds = 60, Enabled = true, ServiceLogoUrl = string.Empty,
            StatusCheckUrl = "https://api.example.com/health",
        };
        toHttp.ProviderConfig["url"] = "https://api.example.com/health";
        toHttp.ProviderConfig["expectedStatusCode"] = "200";
        var asHttp = await svc.AddOrUpdateStatusCheck(toHttp);
        Assert.IsNull(asHttp.HeartbeatToken, "converting away clears the token.");
        Assert.IsFalse(await svc.RecordHeartbeatAsync(oldToken), "the OLD ping URL no longer records.");

        // 3) Convert back to heartbeat → a brand-new token; the old URL stays dead.
        var backToHb = new StatusCheckViewModelBase
        {
            Id = hb.Id, Title = hb.Title, ProviderType = HeartbeatCheckProvider.TypeId,
            IntervalSeconds = 60, Enabled = true, ServiceLogoUrl = string.Empty, StatusCheckUrl = string.Empty,
        };
        backToHb.ProviderConfig["intervalSeconds"] = "3600";
        backToHb.ProviderConfig["graceSeconds"] = "300";
        var asHbAgain = await svc.AddOrUpdateStatusCheck(backToHb);
        Assert.IsFalse(string.IsNullOrEmpty(asHbAgain.HeartbeatToken), "converting back mints a fresh token.");
        Assert.AreNotEqual(oldToken, asHbAgain.HeartbeatToken, "the new token differs from the revoked one.");
        Assert.IsFalse(await svc.RecordHeartbeatAsync(oldToken), "the revoked URL never comes back.");
        Assert.IsTrue(await svc.RecordHeartbeatAsync(asHbAgain.HeartbeatToken!), "the fresh URL works.");
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

    // #320: a service whose registry includes the heartbeat provider (+ http for parity).
    private static StatusCheckService ServiceWithHeartbeat(SuperStatusDb db)
    {
        var factory = StubFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = new CheckProviderRegistry(new ICheckProvider[]
        {
            new HttpCheckProvider(factory),
            new HeartbeatCheckProvider(),
        });
        return Service(db, factory, registry);
    }

    // #320: a persisted heartbeat check with an explicit last-ping time and an attached SLA
    // (so ExecuteStatusCheck has a slow threshold to resolve, like PersistHttpCheck).
    private static StatusCheck PersistHeartbeatCheck(SuperStatusDb db, DateTime lastHeartbeatUtc,
        int intervalSeconds = 3600, int graceSeconds = 300)
    {
        var check = new StatusCheck
        {
            Title = "heartbeat",
            StatusCheckUrl = string.Empty,
            ExpectedStatusCode = 0,
            ProviderType = HeartbeatCheckProvider.TypeId,
            ConfigJson = HbConfig(intervalSeconds, graceSeconds),
            HeartbeatToken = HeartbeatToken.Generate(),
            LastHeartbeatUtc = lastHeartbeatUtc,
            Enabled = true,
            ServiceLogoUrl = string.Empty,
            Created = DateTime.UtcNow,
        };
        SlaTestUtil.Attach(check, 60_000);
        db.StatusCheckSet.Add(check);
        db.SaveChanges();
        return check;
    }

    /// <summary>A provider that records the LastSignalUtc it was handed — to prove pull
    /// providers receive null (only heartbeat checks carry a last-ping timestamp).</summary>
    private sealed class CapturingProvider : ICheckProvider
    {
        public const string Id = "capture";
        public bool Captured { get; private set; }
        public DateTime? LastSignalUtc { get; private set; }

        public ProviderDescriptor Descriptor { get; } = new(
            Id, "Capture", "", new ConfigSchema(1, Array.Empty<ConfigField>()));

        public Task<ProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken = default)
        {
            Captured = true;
            LastSignalUtc = context.LastSignalUtc;
            return Task.FromResult(new ProbeResult { FailType = FailType.NoFail, Reachable = true });
        }
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
