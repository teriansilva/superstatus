using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.StatusCheckOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #200 — the home per-service 30-day strip must paint days with NO samples
/// grey ("gap"), not green. A brand-new check (one day of data, the rest empty)
/// should render exactly one status cell and the remaining days as gap, mirroring
/// the service-detail strip.
/// </summary>
[TestClass]
public class StatusCheckHistoricalGraphTests
{
    private sealed class Stub(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private static BunitTestContext Ctx(string historicalJson)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new Stub(historicalJson)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return ctx;
    }

    // Builds a 30-element history JSON array: day 0 has `total` samples (so it
    // carries a status), the other 29 are empty (total 0 → gap).
    private static string History30(int dataDayTotal, int slow = 0, int down = 0)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 30; i++)
        {
            if (i > 0) sb.Append(',');
            bool dataDay = i == 0;
            int total = dataDay ? dataDayTotal : 0;
            int s = dataDay ? slow : 0;
            int d = dataDay ? down : 0;
            sb.Append($"{{\"statusCheckId\":1,\"date\":\"2026-05-{(i + 1):00}\",\"failedResponseCount\":{d},\"slowResponseCount\":{s},\"unreachableCount\":0,\"total\":{total}}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    [TestMethod]
    public void NewCheck_OneDayOfData_RendersOneCell_RestGrey()
    {
        using var ctx = Ctx(History30(dataDayTotal: 144));
        var cut = ctx.RenderComponent<StatusCheckHistoricalGraph>(p => p
            .Add(c => c.statusCheck, new StatusCheckViewModel { Id = 1, Title = "API" }));

        cut.WaitForAssertion(() => Assert.AreEqual(30, cut.FindAll(".uptime-strip .day").Count));
        var cells = cut.FindAll(".uptime-strip .day");
        int gap = cells.Count(c => c.ClassList.Contains("gap"));
        Assert.AreEqual(29, gap, "29 no-data days are grey gap cells");
        // The single data day is healthy → an "up" cell (no status modifier class).
        Assert.AreEqual(1, cells.Count(c => !c.ClassList.Contains("gap")), "exactly one day carries a status");
    }

    [TestMethod]
    public void DataDayStatus_StillReflectsFailuresAndSlow()
    {
        // The single populated day is degraded (slow) → an amber cell, still 29 gaps.
        using var ctx = Ctx(History30(dataDayTotal: 100, slow: 5));
        var cut = ctx.RenderComponent<StatusCheckHistoricalGraph>(p => p
            .Add(c => c.statusCheck, new StatusCheckViewModel { Id = 1, Title = "API" }));

        cut.WaitForAssertion(() => Assert.AreEqual(30, cut.FindAll(".uptime-strip .day").Count));
        var cells = cut.FindAll(".uptime-strip .day");
        Assert.AreEqual(29, cells.Count(c => c.ClassList.Contains("gap")));
        Assert.AreEqual(1, cells.Count(c => c.ClassList.Contains("degraded")), "the data day is amber");
    }

    // #226: when the parent dashboard list hands the strip data in via Preloaded,
    // the card renders it directly and makes NO per-card API call (the former N+1).
    private sealed class ThrowingStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("API must not be called when Preloaded is supplied");
    }

    private static List<HistoricalStatusDataOverviewChartViewModel> Preloaded30(bool downDay0)
    {
        var list = new List<HistoricalStatusDataOverviewChartViewModel>();
        for (int i = 0; i < 30; i++)
        {
            bool dataDay = i == 0;
            int down = dataDay && downDay0 ? 2 : 0;
            int total = dataDay ? 50 : 0;
            list.Add(new HistoricalStatusDataOverviewChartViewModel(1, new DateOnly(2026, 5, i + 1), down, 0, 0, total));
        }
        return list;
    }

    [TestMethod]
    public void Preloaded_RendersStrip_WithoutCallingApi()
    {
        using var ctx = new BunitTestContext();
        // A stub that throws if hit — proves the strip never fetches when preloaded.
        var http = new HttpClient(new ThrowingStub()) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));

        var cut = ctx.RenderComponent<StatusCheckHistoricalGraph>(p => p
            .Add(c => c.statusCheck, new StatusCheckViewModel { Id = 1, Title = "API" })
            .Add(c => c.Preloaded, Preloaded30(downDay0: true)));

        var cells = cut.FindAll(".uptime-strip .day");
        Assert.AreEqual(30, cells.Count, "renders the full 30-day strip from preloaded data");
        Assert.AreEqual(1, cells.Count(c => c.ClassList.Contains("down")), "the data day is red");
        Assert.AreEqual(29, cells.Count(c => c.ClassList.Contains("gap")));
    }
}
