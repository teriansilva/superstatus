using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #293 Phase D — the read-only SLA summary strip on the public service
/// detail page, between the CURRENT hero and the 30-DAY uptime strip.
///
/// Verdict rules under test:
///   - tick-level 30d uptime ≥ TargetUptimePercent → ✓ COMPLIANT (status-up),
///     compared via SlaDayClassifier.MeetsTarget's decimal cross-multiplication
///     (exact-target boundary is COMPLIANT, not epsilon-under);
///   - below target → ✗ BREACHED (status-down);
///   - Default SLA at exactly 100/100 → neutral "— NO TARGET SET" (the
///     operator never chose a target; a status-colored BREACHED on every
///     default instance would be alarming noise) + "Target —" segment;
///   - no data in the window ("—" headline) → neutral "— NO DATA".
/// The 30d figure is the SAME string the headline "Uptime 30d" chip shows —
/// both come from the single tally in BuildUptimeStrip.
/// </summary>
[TestClass]
public class SlaSummaryStripTests
{
    private const long CheckId = 13;

    // Same dispatch shape as StatusCheckDetailTests.DetailStub, but with the
    // VM's SLA fields and the daily rollups parameterized per test.
    private sealed class Stub : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly StatusCheckViewModel _vm;
        private readonly List<HistoricalStatusDataOverviewChartViewModel> _days;

        public Stub(StatusCheckViewModel vm, List<HistoricalStatusDataOverviewChartViewModel> days)
        {
            _vm = vm;
            _days = days;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.EndsWith("/recent", StringComparison.Ordinal))
            {
                body = "[]"; // non-null empty tick list → page treats the check as found
            }
            else if (path.Contains("/gethistoricaldata", StringComparison.Ordinal))
            {
                body = JsonSerializer.Serialize(_days, Json);
            }
            else // /statuscheck list snapshot
            {
                var paged = new PagedResult<StatusCheckViewModel>
                {
                    Results = new List<StatusCheckViewModel> { _vm },
                    CurrentPage = 1,
                    PageSize = 50,
                    RowCount = 1,
                    PageCount = 1,
                };
                body = JsonSerializer.Serialize(paged, Json);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static StatusCheckViewModel Vm(double target, double critical, long slowMs = 1500, string? slaName = "Messenger 95") => new()
    {
        Id = CheckId,
        Title = "cayoo messenger",
        StatusCheckUrl = "https://example.test/health",
        ExpectedStatusCode = 200,
        IntervalSeconds = 30,
        Enabled = true,
        LinkedSlaName = slaName,
        SlaTargetUptimePercent = target,
        SlaCriticalUptimePercent = critical,
        EffectiveSlowThresholdMs = slowMs,
    };

    /// <summary>30 padded rollup days (today-29..today): leading gap days,
    /// then the given (failed, slow, total) tallies on the most recent days.</summary>
    private static List<HistoricalStatusDataOverviewChartViewModel> ThirtyDays(params (int failed, int slow, int total)[] tail)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = new List<HistoricalStatusDataOverviewChartViewModel>();
        for (int i = 0; i < 30; i++)
        {
            var date = today.AddDays(-29 + i);
            var tailIdx = i - (30 - tail.Length);
            if (tailIdx >= 0)
            {
                var (failed, slow, total) = tail[tailIdx];
                days.Add(new(CheckId, date, failedResponseCount: failed, slowResponseCount: slow, unreachableCount: 0, total: total));
            }
            else
            {
                days.Add(new(CheckId, date, failedResponseCount: 0, slowResponseCount: 0, unreachableCount: 0, total: 0)); // gap
            }
        }
        return days;
    }

    private static IRenderedComponent<StatusCheckDetail> Render(BunitTestContext ctx, StatusCheckViewModel vm, List<HistoricalStatusDataOverviewChartViewModel> days)
    {
        ctx.AddTestAuthorization();
        ctx.Services.AddSingleton(new StatusApiClient(new HttpClient(new Stub(vm, days)) { BaseAddress = new Uri("http://api.test") }));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.RenderComponent<StatusCheckDetail>(p => p.Add(x => x.Id, CheckId));
        cut.WaitForAssertion(() => cut.Find(".sla-card"));
        return cut;
    }

    [TestMethod]
    public void RelaxedSla_GoodUptime_RendersCompliantBadgeAndSegments()
    {
        using var ctx = new BunitTestContext();
        // 998 ok of 1000 → 99.8% ≥ 95% target → COMPLIANT.
        var cut = Render(ctx, Vm(target: 95, critical: 80), ThirtyDays((failed: 2, slow: 0, total: 1000)));

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".sla-card .sla-badge");
            Assert.IsTrue(badge.ClassList.Contains("ok"), "compliant verdict uses the status-up badge");
            StringAssert.Contains(badge.TextContent, "COMPLIANT");

            var line = cut.Find(".sla-card .sla-line").TextContent;
            StringAssert.Contains(line, "Target 95.0%");
            StringAssert.Contains(line, "Slow > 1500 ms");
            StringAssert.Contains(line, "30d 99.8%");

            // Callsign: "SLA // <name>" + READ-ONLY meta.
            var callsign = cut.Find(".sla-card .callsign").TextContent;
            StringAssert.Contains(callsign, "SLA");
            StringAssert.Contains(callsign, "Messenger 95");
            StringAssert.Contains(callsign, "READ-ONLY");
        });
    }

    [TestMethod]
    public void ExactTargetBoundary_IsCompliant_NotEpsilonUnder()
    {
        using var ctx = new BunitTestContext();
        // 999 ok of 1000 vs 99.9% — the binary double for 99.9 sits a hair
        // above the true value; the decimal cross-multiplication must call
        // this COMPLIANT (same boundary contract as SlaDayClassifier).
        var cut = Render(ctx, Vm(target: 99.9, critical: 80), ThirtyDays((failed: 1, slow: 0, total: 1000)));

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".sla-card .sla-badge");
            Assert.IsTrue(badge.ClassList.Contains("ok"), $"999/1000 vs 99.9% is exactly on target → COMPLIANT, got '{badge.TextContent}'");
        });
    }

    [TestMethod]
    public void UptimeBelowTarget_RendersBreachedBadge()
    {
        using var ctx = new BunitTestContext();
        // 998 ok of 1000 → 99.8% < 99.9% target → BREACHED.
        var cut = Render(ctx, Vm(target: 99.9, critical: 80), ThirtyDays((failed: 2, slow: 0, total: 1000)));

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".sla-card .sla-badge");
            Assert.IsTrue(badge.ClassList.Contains("breach"), "below-target verdict uses the status-down badge");
            StringAssert.Contains(badge.TextContent, "BREACHED");
        });
    }

    [TestMethod]
    public void Default100Sla_RendersNeutralNoTargetSet_NeverStatusColored()
    {
        using var ctx = new BunitTestContext();
        // Default 100/100 SLA + a window with one blip: under a real 100%
        // target this would be BREACHED forever — but the operator never
        // chose a target, so the verdict is the neutral NO TARGET SET.
        var cut = Render(ctx, Vm(target: 100, critical: 100, slowMs: 1000, slaName: "Default"), ThirtyDays((failed: 2, slow: 0, total: 1000)));

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".sla-card .sla-badge");
            Assert.IsTrue(badge.ClassList.Contains("neutral"), "default 100/100 SLA renders the neutral badge");
            StringAssert.Contains(badge.TextContent, "NO TARGET SET");
            Assert.AreEqual(0, cut.FindAll(".sla-badge.ok").Count, "no status-up badge for the default SLA");
            Assert.AreEqual(0, cut.FindAll(".sla-badge.breach").Count, "no status-down badge for the default SLA");

            var line = cut.Find(".sla-card .sla-line").TextContent;
            StringAssert.Contains(line, "Target —");
            StringAssert.Contains(line, "Slow > 1000 ms", "the slow threshold still shows for the default SLA");
        });
    }

    [TestMethod]
    public void NoDataWindow_RendersNeutralBadge()
    {
        using var ctx = new BunitTestContext();
        // Configured SLA but a 30d window of pure gaps → "—" headline → neutral.
        var cut = Render(ctx, Vm(target: 95, critical: 80), ThirtyDays());

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".sla-card .sla-badge");
            Assert.IsTrue(badge.ClassList.Contains("neutral"), "no-data window renders the neutral badge");
            StringAssert.Contains(badge.TextContent, "NO DATA");
            Assert.AreEqual(0, cut.FindAll(".sla-badge.ok").Count);
            Assert.AreEqual(0, cut.FindAll(".sla-badge.breach").Count);
            StringAssert.Contains(cut.Find(".sla-card .sla-30d").TextContent, "—");
        });
    }

    [TestMethod]
    public void ThirtyDayFigure_EqualsHeadlineChipString()
    {
        using var ctx = new BunitTestContext();
        // Uneven multi-day tallies (484/487 → 99.4%) so any re-derivation with
        // different rounding would diverge from the headline chip.
        var cut = Render(ctx, Vm(target: 95, critical: 80),
            ThirtyDays((failed: 2, slow: 0, total: 387), (failed: 0, slow: 1, total: 100)));

        cut.WaitForAssertion(() =>
        {
            var chips = cut.FindAll(".chip");
            var headline = chips.Single(c => c.QuerySelector(".k")!.TextContent == "Uptime 30d")
                                .QuerySelector(".v")!.TextContent;
            var slaFigure = cut.Find(".sla-card .sla-30d").TextContent;
            Assert.AreNotEqual("—", headline, "test data must produce a real figure");
            Assert.AreEqual(headline, slaFigure, "the SLA line shows the SAME tick-level string as the headline chip");
        });
    }

    [TestMethod]
    public void SlaPanel_SitsBetweenHeroAndUptimeStrip()
    {
        using var ctx = new BunitTestContext();
        var cut = Render(ctx, Vm(target: 95, critical: 80), ThirtyDays((failed: 0, slow: 0, total: 100)));

        cut.WaitForAssertion(() =>
        {
            var frames = cut.FindAll(".detail-frame");
            Assert.IsTrue(frames.Count >= 3, $"expected hero + sla + strip frames, got {frames.Count}");
            Assert.IsTrue(frames[0].ClassList.Contains("hero"), "first frame is the CURRENT hero");
            Assert.IsTrue(frames[1].ClassList.Contains("sla-card"), "SLA summary sits directly after the hero");
            Assert.IsTrue(frames[2].ClassList.Contains("uptime-card"), "30-DAY strip follows the SLA summary");
        });
    }
}
