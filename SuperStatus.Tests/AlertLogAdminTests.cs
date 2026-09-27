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
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #241/#253 — alerts UI: the AlertDeliveryLog repo failures filter, the
/// AlertLogPanel render + failures-only toggle, and the per-check alert-rule
/// round-trip through AddOrUpdateStatusCheck.
/// </summary>
[TestClass]
public class AlertLogAdminTests
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
        db.StatusCheckSet.Add(new StatusCheck { Id = 1, Title = "acme", StatusCheckUrl = "x", ServiceLogoUrl = "z" });
        var t = new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc);
        db.AlertDeliveryLogSet.AddRange(
            new AlertDeliveryLog { StatusCheckId = 1, AttemptedUtc = t.AddMinutes(1), ChannelTypeId = NotificationChannelTypes.Email, Trigger = AlertTrigger.Outage, Outcome = AlertOutcome.Fired, Reason = "logged only (no delivery channel wired yet)" },
            new AlertDeliveryLog { StatusCheckId = 1, AttemptedUtc = t.AddMinutes(2), ChannelTypeId = NotificationChannelTypes.Email, Trigger = AlertTrigger.Failure, Outcome = AlertOutcome.Skipped, Reason = "throttled" },
            new AlertDeliveryLog { StatusCheckId = 1, AttemptedUtc = t.AddMinutes(3), ChannelTypeId = NotificationChannelTypes.Email, Trigger = AlertTrigger.Outage, Outcome = AlertOutcome.Failed, ErrorMessage = "smtp refused" });
        db.SaveChanges();
    }

    [TestMethod]
    public async Task Repo_GetRecentWithCheck_NewestFirst_IncludesTitle()
    {
        var (db, conn) = Build(); using var _ = db; using var __ = conn;
        Seed(db);
        var rows = await new AlertDeliveryLogRepository(db).GetRecentWithCheckAsync(100, failuresOnly: false);
        Assert.AreEqual(3, rows.Count);
        Assert.IsTrue(rows[0].AttemptedUtc > rows[1].AttemptedUtc, "newest first");
        Assert.AreEqual("acme", rows[0].StatusCheck?.Title);
    }

    [TestMethod]
    public async Task Repo_FailuresOnly_ReturnsOnlyFailed()
    {
        var (db, conn) = Build(); using var _ = db; using var __ = conn;
        Seed(db);
        var rows = await new AlertDeliveryLogRepository(db).GetRecentWithCheckAsync(100, failuresOnly: true);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(AlertOutcome.Failed, rows[0].Outcome);
    }

    [TestMethod]
    public async Task AddOrUpdate_RoundTripsAlertRules_andClampsNegatives()
    {
        var (db, conn) = Build(); using var _ = db; using var __ = conn;
        var svc = new StatusCheckService(
            new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new NoopFactory(), NullLogger<StatusCheckService>.Instance);

        // Create with alert rules (a negative throttle must clamp to 0).
        // #291 Phase D: the legacy email/push fields are gone — delivery
        // targets are linked entities; only the trigger rules stay per-check.
        var created = await svc.AddOrUpdateStatusCheck(new StatusCheckViewModelBase
        {
            Title = "api", StatusCheckUrl = "https://api.test", ServiceLogoUrl = "x",
            AlertOnFailureThreshold = 3, AlertOnOutageMinutes = 5, AlertOnRecovery = true,
            AlertThrottleMinutes = -7,
        });
        Assert.AreEqual(3, created.AlertOnFailureThreshold);
        Assert.AreEqual(5, created.AlertOnOutageMinutes);
        Assert.IsTrue(created.AlertOnRecovery);
        Assert.AreEqual(0, created.AlertThrottleMinutes, "negative clamped to 0");

        // Update changes a threshold + toggles recovery off.
        var updated = await svc.AddOrUpdateStatusCheck(new StatusCheckViewModelBase(created)
        {
            AlertOnFailureThreshold = 1, AlertOnRecovery = false,
        });
        Assert.AreEqual(1, updated.AlertOnFailureThreshold);
        Assert.IsFalse(updated.AlertOnRecovery);
    }

    // ---- bUnit panel ------------------------------------------------------

    private const string AllJson = """
    [
      {"id":1,"statusCheckId":1,"checkTitle":"acme","attemptedUtc":"2026-06-08T10:01:00Z","channel":0,"trigger":2,"outcome":0,"reason":"logged only"},
      {"id":2,"statusCheckId":1,"checkTitle":"acme","attemptedUtc":"2026-06-08T10:02:00Z","channel":0,"trigger":1,"outcome":1,"reason":"throttled"}
    ]
    """;
    private const string FailuresJson = """
    [
      {"id":3,"statusCheckId":1,"checkTitle":"acme","attemptedUtc":"2026-06-08T10:03:00Z","channel":0,"trigger":2,"outcome":2,"errorMessage":"smtp refused"}
    ]
    """;

    [TestMethod]
    public void Panel_RendersRows_WithOutcomeBadges()
    {
        using var ctx = CtxWith(_ => AllJson);
        var cut = ctx.RenderComponent<AlertLogPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".al-row:not(.al-h)").Count));
        Assert.IsTrue(cut.Markup.Contains("FIRED"));
        Assert.IsTrue(cut.Markup.Contains("SKIPPED"));
        cut.Find(".al-row .tag .led.up");        // Fired → green
        Assert.IsTrue(cut.Markup.Contains("throttled"), "skip reason shown");
    }

    [TestMethod]
    public void Panel_FailuresToggle_Refetches_FilteredFeed()
    {
        using var ctx = CtxWith(url => url.Contains("failuresOnly=true") ? FailuresJson : AllJson);
        var cut = ctx.RenderComponent<AlertLogPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".al-row:not(.al-h)").Count));

        cut.Find(".al-toggle").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(1, cut.FindAll(".al-row:not(.al-h)").Count));
        Assert.IsTrue(cut.Markup.Contains("FAILED"));
        cut.Find(".al-row .tag .led.down");      // Failed → red
        Assert.IsFalse(cut.Markup.Contains("FIRED"));
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
