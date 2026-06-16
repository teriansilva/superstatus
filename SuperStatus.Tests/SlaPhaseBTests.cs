using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Hud;
using SuperStatus.Web.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #293 Phase B — SLA-driven day classification. The golden invariant:
/// with the Default SLA (Target 100 / Critical 100) every consumer's output is
/// bit-identical to the historical worst-of-tick rule; only relaxed SLAs change
/// colors. Covers the pure classifier (golden corpus, relaxed targets, exact
/// boundaries), the day-detail tooltip verdict + conditional SLA suffix, the
/// dashboard summary strip, and the public detail page rendering green cells
/// for blip days under a relaxed SLA.
/// </summary>
[TestClass]
public class SlaPhaseBTests
{
    // ---- the golden corpus: (total, down, degraded) day tallies -------------
    // all-up, slow-only, down-only, mixed, gap, single-tick, exact-boundary.
    private static readonly (int Total, int Down, int Degraded)[] Corpus =
    {
        (0, 0, 0),        // gap
        (50, 0, 0),       // all-up
        (50, 0, 3),       // slow-only
        (50, 2, 0),       // down-only
        (387, 2, 1),      // mixed down + slow
        (1, 0, 0),        // single clean tick
        (1, 1, 0),        // single down tick
        (1, 0, 1),        // single slow tick
        (100, 100, 0),    // fully down
        (100, 0, 100),    // fully slow
        (2, 1, 1),        // nothing healthy
        (1000, 1, 0),     // one blip in a busy day
        (1000, 0, 1),     // one slow tick in a busy day
    };

    /// <summary>The OLD worst-of-tick rule, replicated inline as the oracle.</summary>
    private static string WorstOfTick(int total, int down, int degraded)
        => total == 0 ? "gap"
            : down > 0 ? "down"
            : degraded > 0 ? "degraded"
            : "up";

    // ---- pure classifier: golden equivalence --------------------------------

    [TestMethod]
    public void Golden_DefaultSla_BitIdenticalToWorstOfTick_FullCorpus()
    {
        foreach (var (total, down, degraded) in Corpus)
        {
            Assert.AreEqual(
                WorstOfTick(total, down, degraded),
                SlaDayClassifier.Classify(total, down, degraded, targetUptimePercent: 100, criticalUptimePercent: 100),
                $"default SLA must match worst-of-tick for ({total},{down},{degraded})");
        }
    }

    [TestMethod]
    public void Golden_UptimeCell_SlaLessOverload_EqualsExplicitDefault_FullCorpus()
    {
        var date = new DateOnly(2026, 6, 1);
        foreach (var (total, down, degraded) in Corpus)
        {
            // down splits arbitrarily into failed-status vs unreachable — the
            // classifier must treat both as hard failures.
            int unreachable = down / 2;
            var day = new HistoricalStatusDataOverviewChartViewModel(
                1, date, failedResponseCount: down - unreachable, slowResponseCount: degraded,
                unreachableCount: unreachable, total: total);

            Assert.AreEqual(WorstOfTick(total, down, degraded), UptimeCell.From(day),
                $"UptimeCell.From(day) must keep the old vocabulary for ({total},{down},{degraded})");
            Assert.AreEqual(UptimeCell.From(day), UptimeCell.From(day, 100, 100),
                "the SLA-less overload is exactly the explicit 100/100");
        }
    }

    // ---- pure classifier: relaxed SLA ----------------------------------------

    [TestMethod]
    public void RelaxedSla_BlipDay_IsUp_SlowHeavyIsDegraded_HardOutageIsDown()
    {
        // 2 down ticks of 387 → availability == health == 99.48% → up under 95/80.
        Assert.AreEqual("up", SlaDayClassifier.Classify(387, 2, 0, 95, 80));

        // Slow-heavy: availability 99%, health 90% → degraded (health < 95).
        Assert.AreEqual("degraded", SlaDayClassifier.Classify(100, 1, 9, 95, 80));

        // Hard outage: availability 70% < 80 → down.
        Assert.AreEqual("down", SlaDayClassifier.Classify(100, 30, 0, 95, 80));
    }

    [TestMethod]
    public void RelaxedSla_GapStaysGap()
    {
        Assert.AreEqual("gap", SlaDayClassifier.Classify(0, 0, 0, 95, 80));
    }

    // ---- pure classifier: exact boundaries -----------------------------------

    [TestMethod]
    public void Boundary_AvailabilityExactlyCritical_IsNotDown()
    {
        // availability 80/100 == Critical 80 → NOT down; health 80 < 95 → degraded.
        Assert.AreEqual("degraded", SlaDayClassifier.Classify(100, 20, 0, 95, 80));
        // …one more hard failure tips it over.
        Assert.AreEqual("down", SlaDayClassifier.Classify(100, 21, 0, 95, 80));
    }

    [TestMethod]
    public void Boundary_HealthExactlyTarget_IsUp()
    {
        // health 95/100 == Target 95 → up.
        Assert.AreEqual("up", SlaDayClassifier.Classify(100, 0, 5, 95, 80));
        // …one more slow tick tips it under.
        Assert.AreEqual("degraded", SlaDayClassifier.Classify(100, 0, 6, 95, 80));
    }

    [TestMethod]
    public void Boundary_NonBinaryTarget_999_ComparesExactly()
    {
        // 999/1000 health vs Target 99.9: mathematically equal — the decimal
        // cross-multiplication must not call it degraded via the double
        // approximation of 99.9 (which is epsilon-ABOVE 99.9).
        Assert.AreEqual("up", SlaDayClassifier.Classify(1000, 0, 1, 99.9, 80));
        Assert.AreEqual("degraded", SlaDayClassifier.Classify(1000, 0, 2, 99.9, 80));
        // Same on the critical edge: availability 999/1000 vs Critical 99.9.
        Assert.AreEqual("up", SlaDayClassifier.Classify(1000, 1, 0, 99.9, 99.9));
        Assert.AreEqual("down", SlaDayClassifier.Classify(1000, 2, 0, 99.9, 99.9));
    }

    [TestMethod]
    public void Classifier_NullSla_FallsBackToWorstOfTick()
    {
        foreach (var (total, down, degraded) in Corpus)
        {
            Assert.AreEqual(WorstOfTick(total, down, degraded),
                SlaDayClassifier.Classify(total, down, degraded, sla: null));
        }
    }

    // ---- VM carries the SLA targets -------------------------------------------

    [TestMethod]
    public void ViewModel_CarriesSlaTargets_Defaulting100WhenUnlinked()
    {
        var check = new StatusCheck
        {
            Title = "x", StatusCheckUrl = "https://x/health", ServiceLogoUrl = "",
            ExpectedStatusCode = 200,
            Sla = new Sla { Name = "Gold", TargetUptimePercent = 99.9, CriticalUptimePercent = 99, SlowThresholdMs = 250 },
        };
        var vm = new StatusCheckViewModel(check, null);
        Assert.AreEqual(99.9, vm.SlaTargetUptimePercent);
        Assert.AreEqual(99, vm.SlaCriticalUptimePercent);

        check.Sla = null;
        var fallback = new StatusCheckViewModel(check, null);
        Assert.AreEqual(100, fallback.SlaTargetUptimePercent, "no SLA nav → behavior-identical 100");
        Assert.AreEqual(100, fallback.SlaCriticalUptimePercent);

        Assert.AreEqual(100, new StatusCheckViewModel().SlaTargetUptimePercent,
            "fresh/legacy payloads default to 100/100 (old wire shape stays bit-identical)");
    }

    // ---- service layer: day detail + dashboard summary --------------------------

    private sealed class NoopFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static (StatusCheckService svc, SuperStatusDb db, SqliteConnection conn) Build()
    {
        var conn = new SqliteConnection("Filename=:memory:"); conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var svc = new StatusCheckService(
            new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db), new NoopFactory(), NullLogger<StatusCheckService>.Instance);
        return (svc, db, conn);
    }

    private static StatusCheck Check(double target, double critical) => new()
    {
        Title = "API", StatusCheckUrl = "https://api/health", ServiceLogoUrl = "",
        Enabled = true, ExpectedStatusCode = 200, IntervalSeconds = 60,
        Sla = new Sla
        {
            Name = $"SLA {target}/{critical}", TargetUptimePercent = target, CriticalUptimePercent = critical,
            SlowThresholdMs = 1000, CreatedUtc = DateTime.UtcNow,
        },
        Created = DateTime.UtcNow,
    };

    private static void AddTicks(SuperStatusDb db, long checkId, int up, int slow, int down)
    {
        var now = DateTime.UtcNow;
        int i = 0;
        for (int n = 0; n < up; n++) db.HistoricalStatusDataSet.Add(Tick(checkId, now.AddSeconds(-i++), ms: 50));
        for (int n = 0; n < slow; n++) db.HistoricalStatusDataSet.Add(Tick(checkId, now.AddSeconds(-i++), ms: 5000));
        for (int n = 0; n < down; n++) db.HistoricalStatusDataSet.Add(Tick(checkId, now.AddSeconds(-i++), code: 500));
        db.SaveChanges();
    }

    private static HistoricalStatusData Tick(long checkId, DateTime when, int code = 200, long ms = 50) => new()
    {
        StatusCheckId = checkId, TimeOfCheckUTC = when, CheckFailed = false, HttpStatusCode = code, ResponseTimeInMs = ms,
    };

    [TestMethod]
    public async Task DayDetail_RelaxedSla_BlipDayIsUp_AndCarriesTheSlaTarget_RawCountsUntouched()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = Check(target: 95, critical: 80);
        db.StatusCheckSet.Add(check); await db.SaveChangesAsync();
        AddTicks(db, check.Id, up: 19, slow: 0, down: 1);   // availability == health == 95% exactly

        var detail = await svc.GetDayDetailAsync(check.Id, DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.IsNotNull(detail);
        Assert.AreEqual("up", detail!.Status, "availability ≥ 80 and health == 95 == target → up");
        Assert.AreEqual(95, detail.SlaTargetPercent, "non-default SLA → tooltip gets the '(SLA 95%)' suffix data");
        Assert.AreEqual(20, detail.Total, "raw counts stay untouched");
        Assert.AreEqual(1, detail.Down);
        Assert.AreEqual(95.0, detail.UptimePct);
    }

    [TestMethod]
    public async Task DayDetail_DefaultSla_KeepsWorstOfTickVerdict_AndNoSlaSuffix()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = Check(target: 100, critical: 100);
        db.StatusCheckSet.Add(check); await db.SaveChangesAsync();
        AddTicks(db, check.Id, up: 19, slow: 0, down: 1);

        var detail = await svc.GetDayDetailAsync(check.Id, DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.IsNotNull(detail);
        Assert.AreEqual("down", detail!.Status, "100/100 == the old worst-of-tick verdict");
        Assert.IsNull(detail.SlaTargetPercent, "default 100/100 → no suffix → today's exact popover wording");
    }

    [TestMethod]
    public async Task DashboardSummary_StripClassifiesViaEachChecksSla()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var relaxed = Check(target: 95, critical: 80);
        var strict = Check(target: 100, critical: 100);
        db.StatusCheckSet.AddRange(relaxed, strict); await db.SaveChangesAsync();
        // Identical blip days (1 down of 20); only the SLA differs.
        AddTicks(db, relaxed.Id, up: 19, slow: 0, down: 1);
        AddTicks(db, strict.Id, up: 19, slow: 0, down: 1);

        var summary = await svc.GetDashboardSummaryAsync(incidents30dCount: 0);

        var relaxedStrip = summary.PerService.Single(s => s.StatusCheckId == relaxed.Id).Uptime30d;
        var strictStrip = summary.PerService.Single(s => s.StatusCheckId == strict.Id).Uptime30d;
        Assert.AreEqual("up", relaxedStrip[^1], "today's cell: blip within the relaxed SLA → green");
        Assert.AreEqual("down", strictStrip[^1], "default SLA keeps the old worst-of-tick red");
        Assert.AreEqual("gap", relaxedStrip[0], "no-sample days stay gap");

        // #292 invariant: the headline tick-level uptime is NOT SLA-classified —
        // both checks contribute 19 ok of 20 regardless of SLA.
        Assert.AreEqual(95.0, summary.Uptime30dPct, 0.001);
    }

    // ---- popover suffix rendering ------------------------------------------------

    [TestMethod]
    public void Popover_NonDefaultSla_SuffixesTheStatusWord()
    {
        using var ctx = new BunitTestContext();
        Func<DateOnly, Task<DayDetailViewModel?>> loader = d => Task.FromResult<DayDetailViewModel?>(
            new DayDetailViewModel { Date = d, Status = "up", Total = 20, Up = 19, Down = 1, UptimePct = 95, SlaTargetPercent = 95 });

        var cut = ctx.RenderComponent<UptimeStrip>(p => p
            .Add(x => x.Days, new[] { "up" })
            .Add(x => x.StartDate, new DateOnly(2026, 6, 1))
            .Add(x => x.LoadDetail, loader));
        cut.Find(".uptime-strip .day").TriggerEvent("onmouseenter", new MouseEventArgs());

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".day-popover").TextContent, "Operational (SLA 95%)"));
    }

    [TestMethod]
    public void Popover_DefaultSla_KeepsTodaysExactWording()
    {
        using var ctx = new BunitTestContext();
        Func<DateOnly, Task<DayDetailViewModel?>> loader = d => Task.FromResult<DayDetailViewModel?>(
            new DayDetailViewModel { Date = d, Status = "down", Total = 20, Up = 19, Down = 1, UptimePct = 95 });

        var cut = ctx.RenderComponent<UptimeStrip>(p => p
            .Add(x => x.Days, new[] { "down" })
            .Add(x => x.StartDate, new DateOnly(2026, 6, 1))
            .Add(x => x.LoadDetail, loader));
        cut.Find(".uptime-strip .day").TriggerEvent("onmouseenter", new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".day-popover").TextContent, "Down");
            Assert.IsFalse(cut.Find(".day-popover").TextContent.Contains("(SLA"),
                "a 100/100 default shows exactly today's wording");
        });
    }

    // ---- detail page: relaxed-SLA strip renders green blip days --------------------

    private const long DetailCheckId = 31;

    /// <summary>Detail-page stub (the Uptime30LabelTests pattern), now also
    /// serving the SLA target fields on the VM payload. 30 days: day 0 a hard
    /// outage (red), day 1 slow-heavy (amber), every other data day a 2-of-387
    /// blip — green under the 95/80 SLA, red under the old rule.</summary>
    private sealed class RelaxedSlaDetailStub : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.EndsWith("/recent", StringComparison.Ordinal))
            {
                body = "[]";
            }
            else if (path.Contains("/gethistoricaldata", StringComparison.Ordinal))
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var days = new List<HistoricalStatusDataOverviewChartViewModel>();
                for (int i = 0; i < 30; i++)
                {
                    var date = today.AddDays(-29 + i);
                    days.Add(i switch
                    {
                        0 => new(DetailCheckId, date, failedResponseCount: 30, slowResponseCount: 0, unreachableCount: 0, total: 100),  // availability 70 < 80 → down
                        1 => new(DetailCheckId, date, failedResponseCount: 1, slowResponseCount: 9, unreachableCount: 0, total: 100),   // health 90 < 95 → degraded
                        _ => new(DetailCheckId, date, failedResponseCount: 2, slowResponseCount: 0, unreachableCount: 0, total: 387),   // blip → up under 95/80
                    });
                }
                body = JsonSerializer.Serialize(days, Json);
            }
            else // /statuscheck list snapshot — carries the SLA fields (#293 Phase B)
            {
                var vm = new StatusCheckViewModel
                {
                    Id = DetailCheckId,
                    Title = "relaxed",
                    StatusCheckUrl = "https://example.test/health",
                    ExpectedStatusCode = 200,
                    EffectiveSlowThresholdMs = 1000,
                    IntervalSeconds = 30,
                    Enabled = true,
                    SlaTargetUptimePercent = 95,
                    SlaCriticalUptimePercent = 80,
                };
                var paged = new PagedResult<StatusCheckViewModel>
                {
                    Results = new List<StatusCheckViewModel> { vm },
                    CurrentPage = 1, PageSize = 50, RowCount = 1, PageCount = 1,
                };
                body = JsonSerializer.Serialize(paged, Json);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [TestMethod]
    public void DetailPage_RelaxedSla_BlipDaysRenderGreen()
    {
        using var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        ctx.Services.AddSingleton(new StatusApiClient(new HttpClient(new RelaxedSlaDetailStub()) { BaseAddress = new Uri("http://api.test") }));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<StatusCheckDetail>(p => p.Add(x => x.Id, DetailCheckId));
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Uptime strip"), "strip rendered"));

        cut.WaitForAssertion(() =>
        {
            var cells = cut.FindAll(".uptime-strip .day");
            Assert.AreEqual(30, cells.Count);
            Assert.AreEqual(1, cut.FindAll(".uptime-strip .day.down").Count, "only the 70%-availability day is red");
            Assert.AreEqual(1, cut.FindAll(".uptime-strip .day.degraded").Count, "only the 90%-health day is amber");
            Assert.AreEqual(0, cut.FindAll(".uptime-strip .day.gap").Count);
            // 28 blip days (2 down of 387 each) are green = .day with no modifier.
        });
    }
}
