using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;
using SuperStatus.Services.Telemetry;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #86. Verifies the custom status-check instruments emit (via a
/// MeterListener, no live collector) for a successful check, a failed/
/// unreachable check, and an actual webhook fire — with the documented
/// low-cardinality tags.
/// </summary>
[TestClass]
public class StatusCheckMetricsTests
{
    private sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);

    private static (MeterListener listener, ConcurrentQueue<Measurement> sink) Listen()
    {
        var sink = new ConcurrentQueue<Measurement>();
        var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == StatusCheckMetrics.MeterName) l.EnableMeasurementEvents(inst);
            }
        };
        void Record(Instrument inst, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var d = new Dictionary<string, object?>();
            foreach (var t in tags) d[t.Key] = t.Value;
            sink.Enqueue(new Measurement(inst.Name, value, d));
        }
        listener.SetMeasurementEventCallback<long>((inst, v, tags, _) => Record(inst, v, tags));
        listener.SetMeasurementEventCallback<double>((inst, v, tags, _) => Record(inst, v, tags));
        listener.Start();
        return (listener, sink);
    }

    [TestMethod]
    public async Task ExecuteStatusCheck_Success_EmitsExecutedAndDuration_WithNoFailTag()
    {
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var check = PersistCheck(db, expectedStatus: 200);
        var (listener, sink) = Listen();
        using var ___ = listener;

        await Service(db, _ => new HttpResponseMessage(HttpStatusCode.OK)).ExecuteStatusCheck(check);
        listener.Dispose();

        var executed = sink.Single(m => m.Name == "statuschecks.executed");
        Assert.AreEqual(1d, executed.Value);
        Assert.AreEqual(nameof(FailType.NoFail), executed.Tags["fail_type"]);
        Assert.IsTrue(sink.Any(m => m.Name == "statuschecks.duration" && (string)m.Tags["fail_type"]! == nameof(FailType.NoFail)),
            "Duration histogram must record a sample tagged NoFail.");
    }

    [TestMethod]
    public async Task ExecuteStatusCheck_TransportFailure_EmitsUnreachableTag_AndDuration()
    {
        var (db, conn) = Relational();
        using var _ = db; using var __ = conn;
        var check = PersistCheck(db, expectedStatus: 200);
        var (listener, sink) = Listen();
        using var ___ = listener;

        await Service(db, _ => throw new TaskCanceledException("timeout")).ExecuteStatusCheck(check);
        listener.Dispose();

        var executed = sink.Single(m => m.Name == "statuschecks.executed");
        Assert.AreEqual(nameof(FailType.Unreachable), executed.Tags["fail_type"]);
        // Failure path must still record duration so timeouts stay visible.
        Assert.IsTrue(sink.Any(m => m.Name == "statuschecks.duration"),
            "Duration must be recorded on the failure path too.");
    }

    // #343 Phase 4: the RunPostStatusCheckTasks-fires-webhook / webhooks_fired-metric
    // test was removed — webhook delivery is folded into the notification-channel model
    // and fires through AlertEvaluator (covered by the Phase-4 webhook-channel tests),
    // no longer from RunPostStatusCheckTasks.

    [TestMethod]
    public void MeterName_Matches_ServiceDefaultsRegistration()
    {
        // Drift guard: ServiceDefaults registers this exact literal.
        Assert.AreEqual("SuperStatus.Services.StatusCheck", StatusCheckMetrics.MeterName);
    }

    // ---- helpers ----------------------------------------------------------

    private static (SuperStatusDb, SqliteConnection) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck PersistCheck(SuperStatusDb db, int expectedStatus, bool webhook = false)
    {
        var check = new StatusCheck
        {
            Title = "probe",
            StatusCheckUrl = "http://probe.test/health",
            ExpectedStatusCode = expectedStatus,
            Enabled = true,
            ServiceLogoUrl = string.Empty,
            Created = DateTime.UtcNow,
        };
        SlaTestUtil.Attach(check, slowThresholdMs: 60_000);   // #293: classification reads the linked SLA (generous so timing never trips ResponseTime)
        db.StatusCheckSet.Add(check);
        db.SaveChanges();
        // #291 Phase D: dispatch resolves through links only — fixtures link
        // an explicit webhook target (the legacy embedded fields are gone).
        if (webhook) LinkedTargetTestUtil.LinkWebhook(db, check.Id, "http://hook.test/fire");
        return check;
    }

    private static StatusCheckService Service(SuperStatusDb db, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StatusCheckRepository(db),
            new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db),
            new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new StubFactory(responder),
            NullLogger<StatusCheckService>.Instance,
            statusCheckLinkRepository: new StatusCheckLinkRepository(db));

    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return f(request);
            }
        }
    }
}
