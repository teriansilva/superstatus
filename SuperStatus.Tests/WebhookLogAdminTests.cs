using System.Net;
using System.Text;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #107 Phase 2 — admin webhook-execution-log audit UI: the repo
/// failures filter (+ check Include), the service mapping, and the
/// WebhookLogPanel render + failures-only toggle.
/// </summary>
[TestClass]
public class WebhookLogAdminTests
{
    private static (SuperStatusDb db, SqliteConnection conn) Build()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static void Seed(SuperStatusDb db)
    {
        var check = new StatusCheck { Id = 1, Title = "acme", StatusCheckUrl = "x", ServiceLogoUrl = "z" };
        db.StatusCheckSet.Add(check);
        var t = new DateTime(2026, 5, 29, 10, 0, 0, DateTimeKind.Utc);
        db.WebhookExecutionLogSet.AddRange(
            new WebhookExecutionLog { StatusCheckId = 1, AttemptedUtc = t.AddMinutes(1), TargetUrl = "https://h/1", HttpStatusCode = 200, ResponseTimeMs = 40, Outcome = WebhookOutcome.Success },
            new WebhookExecutionLog { StatusCheckId = 1, AttemptedUtc = t.AddMinutes(2), TargetUrl = "https://h/2", HttpStatusCode = 500, ResponseTimeMs = 80, Outcome = WebhookOutcome.NonSuccess, ErrorMessage = "HTTP 500" },
            new WebhookExecutionLog { StatusCheckId = 1, AttemptedUtc = t.AddMinutes(3), TargetUrl = "https://h/3", HttpStatusCode = 0, ResponseTimeMs = 10000, Outcome = WebhookOutcome.Timeout, ErrorMessage = "timeout" },
            new WebhookExecutionLog { StatusCheckId = 1, AttemptedUtc = t.AddMinutes(4), TargetUrl = "https://h/4", HttpStatusCode = 0, ResponseTimeMs = 0, Outcome = WebhookOutcome.Skipped });
        db.SaveChanges();
    }

    [TestMethod]
    public async Task GetRecentWithCheck_All_NewestFirst_IncludesCheckTitle()
    {
        var (db, conn) = Build(); using var _ = db; using var __ = conn;
        Seed(db);
        var rows = await new WebhookExecutionLogRepository(db).GetRecentWithCheckAsync(100, failuresOnly: false);

        Assert.AreEqual(4, rows.Count);
        Assert.IsTrue(rows[0].AttemptedUtc > rows[1].AttemptedUtc, "newest first");
        Assert.AreEqual("acme", rows[0].StatusCheck?.Title, "owning check is eager-loaded for the title column");
    }

    [TestMethod]
    public async Task GetRecentWithCheck_FailuresOnly_ExcludesSuccessAndSkipped()
    {
        var (db, conn) = Build(); using var _ = db; using var __ = conn;
        Seed(db);
        var rows = await new WebhookExecutionLogRepository(db).GetRecentWithCheckAsync(100, failuresOnly: true);

        CollectionAssert.AreEquivalent(
            new[] { WebhookOutcome.NonSuccess, WebhookOutcome.Timeout },
            rows.Select(r => r.Outcome).ToArray());
        Assert.IsFalse(rows.Any(r => r.Outcome is WebhookOutcome.Success or WebhookOutcome.Skipped));
    }

    [TestMethod]
    public async Task Service_MapsRowsToViewModel_WithTitleAndOutcome()
    {
        var (db, conn) = Build(); using var _ = db; using var __ = conn;
        Seed(db);
        var svc = new StatusCheckService(
            new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new NoopFactory(), NullLogger<StatusCheckService>.Instance);

        var vms = await svc.GetRecentWebhookLogAsync(100, failuresOnly: false);

        Assert.AreEqual(4, vms.Count);
        Assert.IsTrue(vms.All(v => v.CheckTitle == "acme"));
    }

    // ---- bUnit panel ------------------------------------------------------

    private const string AllJson = """
    [
      {"id":3,"statusCheckId":1,"checkTitle":"acme","attemptedUtc":"2026-05-29T10:03:00Z","targetUrl":"https://h/3","httpStatusCode":0,"responseTimeMs":10000,"outcome":2,"errorMessage":"timeout"},
      {"id":1,"statusCheckId":1,"checkTitle":"acme","attemptedUtc":"2026-05-29T10:01:00Z","targetUrl":"https://h/1","httpStatusCode":200,"responseTimeMs":40,"outcome":0}
    ]
    """;
    private const string FailuresJson = """
    [
      {"id":3,"statusCheckId":1,"checkTitle":"acme","attemptedUtc":"2026-05-29T10:03:00Z","targetUrl":"https://h/3","httpStatusCode":0,"responseTimeMs":10000,"outcome":2,"errorMessage":"timeout"}
    ]
    """;

    [TestMethod]
    public void Panel_RendersRows_WithOutcomeBadges()
    {
        using var ctx = CtxWith(_ => AllJson);
        var cut = ctx.RenderComponent<WebhookLogPanel>();

        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wl-row:not(.wl-h)").Count));
        Assert.IsTrue(cut.Markup.Contains("SUCCESS"));
        Assert.IsTrue(cut.Markup.Contains("TIMEOUT"));
        cut.Find(".wl-row .tag .led.up");        // Success → green
        cut.Find(".wl-row .tag .led.degraded");  // Timeout → amber
    }

    [TestMethod]
    public void Panel_FailuresToggle_Refetches_FilteredFeed()
    {
        // Stub returns the failures-only feed when the query asks for it.
        using var ctx = CtxWith(url => url.Contains("failuresOnly=true") ? FailuresJson : AllJson);
        var cut = ctx.RenderComponent<WebhookLogPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wl-row:not(.wl-h)").Count));

        cut.Find(".wl-toggle").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(1, cut.FindAll(".wl-row:not(.wl-h)").Count));
        Assert.IsTrue(cut.Markup.Contains("TIMEOUT"));
        Assert.IsFalse(cut.Markup.Contains("SUCCESS"));
    }

    private static BunitTestContext CtxWith(Func<string, string> jsonForUrl)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new RoutingStubHandler(jsonForUrl)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return ctx;
    }

    private sealed class RoutingStubHandler(Func<string, string> jsonForUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(jsonForUrl(request.RequestUri!.ToString()), Encoding.UTF8, "application/json") });
    }

    private sealed class NoopFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Noop());
        private sealed class Noop : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
