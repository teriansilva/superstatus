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
/// Regression: the detail page's "Uptime 30d" chip must be TICK-level uptime
/// (ok ticks / total ticks, the same semantic as the day tooltip's UptimePct),
/// not the share of fully-green DAYS. The day-share formula made a young check
/// whose every day contained one brief blip read 0.0% while each tooltip said
/// ~99.5% — observed live on the Cayoo Messenger check (2 down ticks out of
/// 387 in each of its 2 known days → headline 0.0%).
/// </summary>
[TestClass]
public class Uptime30LabelTests
{
    private const long CheckId = 11;

    // Two data days, neither fully green, weighted differently:
    //   day A: 385 ok of 387 (2 bad-status ticks)        → cell "down"
    //   day B:  99 ok of 100 (1 slow tick)               → cell "degraded"
    // Tick-level: (385+99) / (387+100) = 484/487 = 99.38… → "99.4%".
    // Day-share (the bug): 0 green days of 2 known → "0.0%".
    private sealed class Stub : HttpMessageHandler
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
                        28 => new(CheckId, date, failedResponseCount: 2, slowResponseCount: 0, unreachableCount: 0, total: 387),
                        29 => new(CheckId, date, failedResponseCount: 0, slowResponseCount: 1, unreachableCount: 0, total: 100),
                        _  => new(CheckId, date, failedResponseCount: 0, slowResponseCount: 0, unreachableCount: 0, total: 0), // gap
                    });
                }
                body = JsonSerializer.Serialize(days, Json);
            }
            else
            {
                var vm = new StatusCheckViewModel
                {
                    Id = CheckId,
                    Title = "cayoo messenger",
                    StatusCheckUrl = "https://example.test/health",
                    ExpectedStatusCode = 200,
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

    [TestMethod]
    public void Uptime30Chip_IsTickLevel_NotGreenDayShare()
    {
        using var ctx = new BunitTestContext();
        ctx.AddTestAuthorization();
        ctx.Services.AddSingleton(new StatusApiClient(new HttpClient(new Stub()) { BaseAddress = new Uri("http://api.test") }));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<StatusCheckDetail>(p => p.Add(x => x.Id, CheckId));
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Uptime 30d"), "uptime chip rendered"));

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("99.4%"),
                "headline = tick-level uptime (484/487 ticks), weighted across days");
            Assert.IsFalse(cut.Markup.Contains("0.0%"),
                "the green-day-share formula (0 fully-green days → 0.0%) must be gone");
        });
    }
}
