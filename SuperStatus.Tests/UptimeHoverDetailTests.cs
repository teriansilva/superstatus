using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Hud;
using SuperStatus.Web.Components.StatusCheckOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #201 — the lazy hover-detail popover on the uptime strip: the strip's
/// loading/loaded/no-data states, and (via the home graph) that hovering fetches
/// a day exactly once and re-hovering uses the parent cache.
/// </summary>
[TestClass]
public class UptimeHoverDetailTests
{
    // ---------- UptimeStrip popover states ----------

    [TestMethod]
    public void Hover_LoadsAndRendersDetail()
    {
        using var ctx = new BunitTestContext();
        int calls = 0;
        Func<DateOnly, Task<DayDetailViewModel?>> loader = d =>
        {
            calls++;
            return Task.FromResult<DayDetailViewModel?>(new DayDetailViewModel
            { Date = d, Status = "up", Total = 10, Up = 10, UptimePct = 100 });
        };

        var cut = ctx.RenderComponent<UptimeStrip>(p => p
            .Add(x => x.Days, new[] { "up", "up", "up" })
            .Add(x => x.StartDate, new DateOnly(2026, 5, 1))
            .Add(x => x.LoadDetail, loader));

        cut.FindAll(".uptime-strip .day")[1].TriggerEvent("onmouseenter", new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find(".day-popover")));
        Assert.AreEqual(1, calls, "hover triggers exactly one load");
        var pop = cut.Find(".day-popover");
        StringAssert.Contains(pop.TextContent, "Operational");
        StringAssert.Contains(pop.TextContent, "May 2");   // StartDate + index 1
    }

    [TestMethod]
    public void Hover_GapDay_ShowsNoDataMessage()
    {
        using var ctx = new BunitTestContext();
        Func<DateOnly, Task<DayDetailViewModel?>> loader = d =>
            Task.FromResult<DayDetailViewModel?>(new DayDetailViewModel { Date = d, Status = "gap", Total = 0 });

        var cut = ctx.RenderComponent<UptimeStrip>(p => p
            .Add(x => x.Days, new[] { "gap" })
            .Add(x => x.StartDate, new DateOnly(2026, 5, 1))
            .Add(x => x.LoadDetail, loader));

        cut.Find(".uptime-strip .day").TriggerEvent("onmouseenter", new MouseEventArgs());

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".day-popover").TextContent, "no data for this day"));
    }

    [TestMethod]
    public void OutOfOrderLoads_DoNotShowAStaleCellsDetail()
    {
        // Hermes review: a slower cell's load resolving AFTER the user moved to
        // another cell must not overwrite the now-active cell's popover.
        using var ctx = new BunitTestContext();
        var pending = new Dictionary<DateOnly, TaskCompletionSource<DayDetailViewModel?>>();
        Func<DateOnly, Task<DayDetailViewModel?>> loader = d =>
        {
            var tcs = new TaskCompletionSource<DayDetailViewModel?>();
            pending[d] = tcs;
            return tcs.Task;
        };
        var start = new DateOnly(2026, 5, 1);

        var cut = ctx.RenderComponent<UptimeStrip>(p => p
            .Add(x => x.Days, new[] { "down", "up" })
            .Add(x => x.StartDate, start)
            .Add(x => x.LoadDetail, loader));

        // Re-find before each trigger (the first hover re-renders, invalidating
        // the earlier element/handler refs).
        cut.InvokeAsync(() => cut.FindAll(".uptime-strip .day")[0].TriggerEvent("onmouseenter", new MouseEventArgs())); // cell 0 pending
        cut.InvokeAsync(() => cut.FindAll(".uptime-strip .day")[1].TriggerEvent("onmouseenter", new MouseEventArgs())); // cell 1 active, pending

        // Cell 1 resolves first → its detail shows.
        cut.InvokeAsync(() => pending[start.AddDays(1)].SetResult(
            new DayDetailViewModel { Date = start.AddDays(1), Status = "up", Total = 5, Up = 5, UptimePct = 100 }));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".day-popover").TextContent, "Operational"));

        // Cell 0 resolves LATE → must be discarded (popover stays on cell 1).
        cut.InvokeAsync(() => pending[start].SetResult(
            new DayDetailViewModel { Date = start, Status = "down", Total = 5, Up = 0, Down = 5, UptimePct = 0 }));

        var pop = cut.Find(".day-popover");
        StringAssert.Contains(pop.TextContent, "Operational");
        Assert.IsFalse(pop.TextContent.Contains("Down"), "the late cell-0 result must not replace the active cell-1 popover");
    }

    [TestMethod]
    public void NonInteractive_RendersNoPopoverAndNoHandlers()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<UptimeStrip>(p => p.Add(x => x.Days, new[] { "up", "down" }));
        Assert.AreEqual(0, cut.FindAll(".day-popover").Count);
        // Non-interactive cell keeps the original bare class (no tabindex).
        Assert.IsNull(cut.Find(".uptime-strip .day").GetAttribute("tabindex"));
    }

    // ---------- home graph: lazy fetch once + cache ----------

    private sealed class RoutingStub : HttpMessageHandler
    {
        public int DayCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.Contains("/gethistoricaldata/"))
            {
                // 30 days; the oldest (index 0) carries data, the rest are empty.
                var sb = new StringBuilder("[");
                for (int i = 0; i < 30; i++)
                {
                    if (i > 0) sb.Append(',');
                    int total = i == 0 ? 144 : 0;
                    sb.Append($"{{\"statusCheckId\":1,\"date\":\"2026-05-{(i + 1):00}\",\"failedResponseCount\":0,\"slowResponseCount\":0,\"unreachableCount\":0,\"total\":{total}}}");
                }
                sb.Append(']');
                json = sb.ToString();
            }
            else if (path.Contains("/day/"))
            {
                DayCalls++;
                json = "{\"statusCheckId\":1,\"date\":\"2026-05-01\",\"status\":\"up\",\"total\":144,\"up\":144,\"degraded\":0,\"down\":0,\"unreachable\":0,\"uptimePct\":100}";
            }
            else { json = "[]"; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [TestMethod]
    public void Graph_HoverFetchesDayOnce_AndCachesOnRehover()
    {
        var stub = new RoutingStub();
        using var ctx = new BunitTestContext();
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));

        var cut = ctx.RenderComponent<StatusCheckHistoricalGraph>(p => p
            .Add(c => c.statusCheck, new StatusCheckViewModel { Id = 1, Title = "API" }));

        // Wait for the strip (history) to load.
        cut.WaitForAssertion(() => Assert.AreEqual(30, cut.FindAll(".uptime-strip .day").Count));

        // Hover the oldest (data) cell twice.
        cut.FindAll(".uptime-strip .day")[0].TriggerEvent("onmouseenter", new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find(".day-popover")));
        cut.FindAll(".uptime-strip .day")[0].TriggerEvent("onmouseleave", new MouseEventArgs());
        cut.FindAll(".uptime-strip .day")[0].TriggerEvent("onmouseenter", new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find(".day-popover")));

        Assert.AreEqual(1, stub.DayCalls, "the day detail is fetched once and re-hover uses the parent cache");
    }
}
