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
/// Issue #179 — the service-detail page is status-only for EVERY viewer. The
/// check config (endpoint URL, latency threshold, the Check-parameters panel) is
/// managed in the operator dashboard (/admin) and never rendered here — for
/// anonymous or authenticated users alike (no AuthorizeView gating).
/// </summary>
[TestClass]
public class StatusCheckDetailTests
{
    private const long CheckId = 7;
    private const string SecretUrl = "https://secret-endpoint.internal/health";

    // The detail page loads via GetRecentTicksAsync (non-null = found) then
    // GetStatusAsync for the VM snapshot. Stub both.
    private sealed class DetailStub : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
                // #223: the 30-day strip is built from these padded daily rollups.
                body = HistoricalThirtyDaysJson(Json);
            }
            else // /statuscheck list snapshot
            {
                var vm = new StatusCheckViewModel
                {
                    Id = CheckId,
                    Title = "acme",
                    StatusCheckUrl = SecretUrl,
                    ExpectedStatusCode = 200,
                    ExpectedResponseTimeInMs = 1234,
                    IntervalSeconds = 30,
                    Enabled = true,
                };
                var paged = new PagedResult<StatusCheckViewModel>
                {
                    Results = new List<StatusCheckViewModel> { vm },
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

    private static BunitTestContext Ctx(bool authenticated)
    {
        var ctx = new BunitTestContext();
        var auth = ctx.AddTestAuthorization();
        if (authenticated) auth.SetAuthorized("operator");
        ctx.Services.AddSingleton(new StatusApiClient(new HttpClient(new DetailStub()) { BaseAddress = new Uri("http://api.test") }));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // #175 (follow-up): the service-detail page is status-only for EVERYONE —
    // endpoint URL / threshold / the Check-parameters panel are never shown
    // (operators manage config in /admin). Asserted for both anon + authed.

    [TestMethod]
    public void Anonymous_ShowsNoConfig()
    {
        using var ctx = Ctx(authenticated: false);
        AssertNoConfig(ctx);
    }

    [TestMethod]
    public void Authenticated_AlsoShowsNoConfig()
    {
        using var ctx = Ctx(authenticated: true);
        AssertNoConfig(ctx);
    }

    private static void AssertNoConfig(BunitTestContext ctx)
    {
        var cut = ctx.RenderComponent<StatusCheckDetail>(p => p.Add(x => x.Id, CheckId));
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("CURRENT"), "hero rendered"));

        Assert.IsFalse(cut.Markup.Contains(SecretUrl), "endpoint URL is never shown on the detail page");
        Assert.IsFalse(cut.Markup.Contains("Check parameters"), "the CONFIG panel is gone (config lives in /admin)");
        Assert.IsFalse(cut.Markup.Contains("1234 ms"), "latency threshold is never shown on the detail page");
        Assert.IsFalse(cut.Markup.Contains("Poll interval"), "poll interval is never shown on the detail page");
        // Live status the page DOES keep:
        cut.Find(".hero");
        Assert.IsTrue(cut.Markup.Contains("Uptime 30d"));
        Assert.IsTrue(cut.Markup.Contains("Tick history"));
    }

    // #223: the 30-day strip must reflect the daily rollups (GetHistoricalStatusData),
    // not the 8 recent ticks — which only cover today and left 29 grey cells. Given a
    // populated 30-day rollup, the strip renders many cells across multiple colours.
    [TestMethod]
    public void UptimeStrip_RendersAllRollupDays_NotJustToday()
    {
        using var ctx = Ctx(authenticated: false);
        var cut = ctx.RenderComponent<StatusCheckDetail>(p => p.Add(x => x.Id, CheckId));
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Uptime strip"), "strip rendered"));

        cut.WaitForAssertion(() =>
        {
            var cells = cut.FindAll(".uptime-strip .day");
            Assert.IsTrue(cells.Count >= 25, $"expected ~30 day cells, got {cells.Count}");

            var down = cut.FindAll(".uptime-strip .day.down").Count;
            var degraded = cut.FindAll(".uptime-strip .day.degraded").Count;
            var gap = cut.FindAll(".uptime-strip .day.gap").Count;
            var up = cells.Count - down - degraded - gap; // up = .day with no modifier

            Assert.IsTrue(down >= 1, "at least one down day rendered");
            Assert.IsTrue(degraded >= 1, "at least one degraded day rendered");
            Assert.IsTrue(up >= 5, $"expected several up days, got {up} (regression: only today filled)");
        });
    }

    // 30 padded daily rollups (today-29..today): a mix of up/degraded/down + two
    // no-sample (gap) days, serialized as the gethistoricaldata endpoint returns them.
    private static string HistoricalThirtyDaysJson(JsonSerializerOptions json)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = new List<HistoricalStatusDataOverviewChartViewModel>();
        for (int i = 0; i < 30; i++)
        {
            var date = today.AddDays(-29 + i);
            if (i % 7 == 0)
                days.Add(new(CheckId, date, failedResponseCount: 2, slowResponseCount: 0, unreachableCount: 0, total: 50)); // down
            else if (i % 5 == 0)
                days.Add(new(CheckId, date, failedResponseCount: 0, slowResponseCount: 3, unreachableCount: 0, total: 50)); // degraded
            else if (i is 3 or 11)
                days.Add(new(CheckId, date, failedResponseCount: 0, slowResponseCount: 0, unreachableCount: 0, total: 0)); // gap (no samples)
            else
                days.Add(new(CheckId, date, failedResponseCount: 0, slowResponseCount: 0, unreachableCount: 0, total: 50)); // up
        }
        return JsonSerializer.Serialize(days, json);
    }
}
