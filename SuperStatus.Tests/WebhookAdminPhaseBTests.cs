using System.Net;
using System.Text;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
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
/// Issue #291 Phase B — the Webhooks admin tab surface: the WebhookListPanel
/// (rows + used-by counts, empty state, delete guard, edit dialog validation),
/// the test-fire executor path (shared with real dispatch, result surfaced
/// inline, NO audit row), the API client mappings, and the webhook-name
/// resolution on the relocated execution log.
/// </summary>
[TestClass]
public class WebhookAdminPhaseBTests
{
    // ---- stubbed /admin/webhooks feeds -------------------------------------

    private const string TwoWebhooksJson = """
    [
      {"id":1,"name":"Ops hook","url":"https://hooks.ex.com/err","isEnabled":true,"throttleMinutes":0,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":4,"linkedCheckNames":["Public API","Web","DB","Mail"]}},
      {"id":2,"name":"Old pager","url":"https://pg.ex.com/x","isEnabled":false,"throttleMinutes":5,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":0,"linkedCheckNames":[]}}
    ]
    """;

    private const string DeleteConflictJson = """
    {"message":"Webhook 'Ops hook' is linked to 4 check(s); unlink it first.","usage":{"usedByCount":4,"linkedCheckNames":["Public API","Web","DB","Mail"]}}
    """;

    private sealed record Recorded(string Method, string Path);

    private static (BunitTestContext ctx, List<Recorded> requests) Ctx(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var ctx = new BunitTestContext();
        var requests = new List<Recorded>();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new RoutingHandler(requests, responder)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return (ctx, requests);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage DefaultRoutes(HttpRequestMessage request)
        => request.RequestUri!.AbsolutePath == "/admin/webhooks" && request.Method == HttpMethod.Get
            ? Json(TwoWebhooksJson)
            : Json("{}");

    // ---- list panel ---------------------------------------------------------

    [TestMethod]
    public void ListPanel_RendersRows_WithUsedByCountsAndState()
    {
        var (ctx, _) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<WebhookListPanel>();

        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wh-row:not(.wh-h)").Count));
        Assert.IsTrue(cut.Markup.Contains("Ops hook"));
        Assert.IsTrue(cut.Markup.Contains("https://hooks.ex.com/err"));
        Assert.IsTrue(cut.Markup.Contains("4 checks"), "used-by count from the embedded LinkedEntitySummary");
        Assert.IsTrue(cut.Markup.Contains("0 checks"), "unlinked target shows zero");
        cut.Find(".wh-state .tag .led.up");        // enabled → green LED
        cut.Find(".wh-state .tag .led.unknown");   // disabled → neutral LED
        Assert.IsTrue(cut.Markup.Contains("Disable"), "enabled row offers Disable");
        Assert.IsTrue(cut.Markup.Contains("Enable"), "disabled row offers Enable");
        Assert.IsTrue(cut.FindAll("button").Any(b => b.TextContent.Trim() == "+ Add webhook"), "header add button");
    }

    [TestMethod]
    public void ListPanel_EmptyState_ShowsExactCopy_AndAddButton()
    {
        var (ctx, _) = Ctx(_ => Json("[]"));
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<WebhookListPanel>();

        cut.WaitForAssertion(() => Assert.IsTrue(
            cut.Markup.Contains("No webhooks yet — create one to notify external systems when a check fails."),
            "the deliberate empty-state copy, verbatim"));
        var addButtons = cut.FindAll("button").Where(b => b.TextContent.Trim() == "+ Add webhook").ToList();
        Assert.AreEqual(1, addButtons.Count, "empty state carries its own add button");
    }

    [TestMethod]
    public void Delete_LinkedWebhook_RendersBlockedDialog_FromThe409Payload_CancelOnly()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/admin/webhooks/1"
                ? Json(DeleteConflictJson, HttpStatusCode.Conflict)
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<WebhookListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wh-row:not(.wh-h)").Count));

        // Row 1 is linked (4 checks) — Delete goes to the API and the 409
        // LinkedEntitySummary payload drives the blocked dialog.
        cut.FindAll(".wh-link.danger")[0].Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("DELETE BLOCKED"));
            Assert.IsTrue(provider.Markup.Contains("Public API"), "linked check names listed");
            Assert.IsTrue(provider.Markup.Contains("Mail"), "all linked check names listed");
        });
        Assert.IsTrue(requests.Any(r => r is { Method: "DELETE", Path: "/admin/webhooks/1" }));

        // The only offered action is Cancel — no confirm/unlink-all in this phase.
        var actions = provider.FindAll(".mud-dialog-actions button");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("Cancel", actions[0].TextContent.Trim());
    }

    [TestMethod]
    public void Delete_UnlinkedWebhook_ConfirmsThenDeletes()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/admin/webhooks/2"
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<WebhookListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wh-row:not(.wh-h)").Count));

        // Row 2 is unlinked — a plain destructive confirm precedes the DELETE.
        cut.FindAll(".wh-link.danger")[1].Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("Delete Old pager?")));
        Assert.IsFalse(requests.Any(r => r.Method == "DELETE"), "nothing deleted before the confirm");

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Delete").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(
            requests.Any(r => r is { Method: "DELETE", Path: "/admin/webhooks/2" })));
    }

    // ---- edit dialog --------------------------------------------------------

    [TestMethod]
    public void EditDialog_RequiresNameAndUrl_NoPostWhenInvalid()
    {
        var (ctx, requests) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<WebhookListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wh-row:not(.wh-h)").Count));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "+ Add webhook").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("ADD WEBHOOK")));

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("Name is required."));
            Assert.IsTrue(provider.Markup.Contains("URL is required."));
        });
        Assert.IsFalse(requests.Any(r => r.Method == "POST"), "invalid form never posts");
        Assert.IsTrue(provider.Markup.Contains("0 = trigger every failure."), "throttle helper text");
    }

    [TestMethod]
    public void EditDialog_ExistingWebhook_ShowsReadOnlyLinkedChecks_AndPostsOnSave()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/admin/webhooks"
                ? Json("""{"id":1,"name":"Ops hook","url":"https://hooks.ex.com/err","isEnabled":true,"throttleMinutes":0,"usage":{"usedByCount":4,"linkedCheckNames":["Public API","Web","DB","Mail"]}}""")
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<WebhookListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wh-row:not(.wh-h)").Count));

        cut.FindAll(".wh-link").First(b => b.TextContent.Trim() == "Edit").Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("EDIT WEBHOOK"));
            Assert.IsTrue(provider.Markup.Contains("Linked checks: Public API, Web, DB, Mail"),
                "read-only linked-checks line when editing an existing webhook");
        });

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/admin/webhooks" }), "saves via POST /admin/webhooks");
            Assert.IsFalse(provider.Markup.Contains("EDIT WEBHOOK"), "dialog closed after save");
        });
    }

    // ---- test-fire ----------------------------------------------------------

    [TestMethod]
    public void TestFire_RowAction_PostsToTestEndpoint()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/admin/webhooks/1/test"
                ? Json("""{"outcome":0,"httpStatusCode":200,"responseTimeMs":12,"errorMessage":null,"targetUrl":"https://hooks.ex.com/err"}""")
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<WebhookListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wh-row:not(.wh-h)").Count));

        cut.FindAll(".wh-link").First(b => b.TextContent.Trim() == "Test-fire").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(
            requests.Any(r => r is { Method: "POST", Path: "/admin/webhooks/1/test" })));
    }

    [TestMethod]
    public async Task TestFire_Service_FiresThroughDispatchExecutor_NoLogRowWritten()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var webhook = new Webhook { Name = "Ops hook", Url = "https://hooks.test/err", IsEnabled = true, CreatedUtc = DateTime.UtcNow };
        db.WebhookSet.Add(webhook);
        await db.SaveChangesAsync();

        var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await Svc(db, factory).TestFireWebhookAsync(webhook);

        Assert.AreEqual(WebhookOutcome.Success, result.Outcome);
        Assert.AreEqual(200, result.HttpStatusCode);
        Assert.AreEqual("https://hooks.test/err", result.TargetUrl);
        Assert.IsNull(result.ErrorMessage);
        CollectionAssert.AreEqual(new[] { new Uri("https://hooks.test/err") }, factory.Requested, "the executor hit the target once");
        // The wire result surfaces inline only: the execution log's StatusCheckId
        // is a required FK and a test has no triggering check (no schema change
        // in this phase) — so no audit row is written.
        Assert.AreEqual(0, await db.WebhookExecutionLogSet.CountAsync(), "test fires write no execution-log row");
    }

    [TestMethod]
    public async Task TestFire_Service_MapsNonSuccessAndTransportFailure()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var webhook = new Webhook { Name = "w", Url = "https://hooks.test/err", IsEnabled = false, CreatedUtc = DateTime.UtcNow };

        // Disabled targets can still be test-fired (a deliberate operator probe).
        var nonSuccess = await Svc(db, new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).TestFireWebhookAsync(webhook);
        Assert.AreEqual(WebhookOutcome.NonSuccess, nonSuccess.Outcome);
        Assert.AreEqual(500, nonSuccess.HttpStatusCode);
        StringAssert.Contains(nonSuccess.ErrorMessage, "HTTP 500");

        var transport = await Svc(db, new RecordingFactory(_ => throw new HttpRequestException("boom"))).TestFireWebhookAsync(webhook);
        Assert.AreEqual(WebhookOutcome.TransportFailure, transport.Outcome);
        Assert.AreEqual(0, transport.HttpStatusCode);
        StringAssert.Contains(transport.ErrorMessage, "boom");

        Assert.AreEqual(0, await db.WebhookExecutionLogSet.CountAsync());
    }

    [TestMethod]
    public async Task Client_TestFire_ParsesResult_And404ToNull()
    {
        var ok = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => Json("""{"outcome":1,"httpStatusCode":503,"responseTimeMs":40,"errorMessage":"HTTP 503 Service Unavailable","targetUrl":"https://h/x"}""")))
        { BaseAddress = new Uri("http://api.test") });
        var result = await ok.TestFireWebhookAsync(1);
        Assert.IsNotNull(result);
        Assert.AreEqual(WebhookOutcome.NonSuccess, result.Outcome);
        Assert.AreEqual(503, result.HttpStatusCode);

        var missing = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsNull(await missing.TestFireWebhookAsync(99));
    }

    [TestMethod]
    public async Task Client_Delete_Maps204_404_And409WithUsage()
    {
        var deleted = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue((await deleted.DeleteWebhookAsync(1)).Deleted);

        var missing = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue((await missing.DeleteWebhookAsync(2)).NotFound);

        var blocked = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => Json(DeleteConflictJson, HttpStatusCode.Conflict)))
        { BaseAddress = new Uri("http://api.test") });
        var conflict = await blocked.DeleteWebhookAsync(3);
        Assert.IsFalse(conflict.Deleted);
        Assert.IsNotNull(conflict.BlockedBy);
        Assert.AreEqual(4, conflict.BlockedBy.UsedByCount);
        CollectionAssert.AreEqual(new[] { "Public API", "Web", "DB", "Mail" }, conflict.BlockedBy.LinkedCheckNames);
    }

    // ---- webhook name on the relocated execution log ------------------------

    [TestMethod]
    public async Task WebhookLog_ResolvesWebhookName_ViaTheExistingRepositoryRead()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = new StatusCheck { Title = "acme", StatusCheckUrl = "x", ServiceLogoUrl = "" };
        var webhook = new Webhook { Name = "Ops hook", Url = "https://h/1", CreatedUtc = DateTime.UtcNow };
        db.StatusCheckSet.Add(check);
        db.WebhookSet.Add(webhook);
        await db.SaveChangesAsync();
        db.WebhookExecutionLogSet.AddRange(
            new WebhookExecutionLog { StatusCheckId = check.Id, WebhookId = webhook.Id, AttemptedUtc = DateTime.UtcNow, TargetUrl = "https://h/1", HttpStatusCode = 200, ResponseTimeMs = 10, Outcome = WebhookOutcome.Success },
            // pre-#291 row: no WebhookId → name resolves to null, UI shows "—".
            new WebhookExecutionLog { StatusCheckId = check.Id, AttemptedUtc = DateTime.UtcNow.AddMinutes(-1), TargetUrl = "https://h/legacy", HttpStatusCode = 200, ResponseTimeMs = 10, Outcome = WebhookOutcome.Success });
        await db.SaveChangesAsync();

        var vms = await Svc(db, new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)))
            .GetRecentWebhookLogAsync(100, failuresOnly: false);

        Assert.AreEqual(2, vms.Count);
        Assert.AreEqual("Ops hook", vms[0].WebhookName, "linked target's name is joined in the repository read");
        Assert.IsNull(vms[1].WebhookName, "pre-#291 rows have no webhook name");
    }

    [TestMethod]
    public void WebhookLogPanel_RendersWebhookNameColumn()
    {
        var (ctx, _) = Ctx(request => request.RequestUri!.AbsolutePath == "/admin/webhook-log"
            ? Json("""
              [
                {"id":1,"statusCheckId":1,"checkTitle":"acme","webhookId":7,"webhookName":"Ops hook","attemptedUtc":"2026-06-01T10:00:00Z","targetUrl":"https://h/1","httpStatusCode":200,"responseTimeMs":40,"outcome":0},
                {"id":2,"statusCheckId":1,"checkTitle":"acme","attemptedUtc":"2026-06-01T09:00:00Z","targetUrl":"https://h/legacy","httpStatusCode":200,"responseTimeMs":40,"outcome":0}
              ]
              """)
            : Json("{}"));
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<WebhookLogPanel>();

        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".wl-row:not(.wl-h)").Count));
        var names = cut.FindAll(".wl-hook").Select(e => e.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Ops hook", "—" }, names);
        Assert.IsTrue(cut.Markup.Contains("Webhook"), "the log table grew a Webhook column");
    }

    // ---- fixtures -----------------------------------------------------------

    private static (SuperStatusDb db, SqliteConnection conn) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheckService Svc(SuperStatusDb db, IHttpClientFactory factory)
        => new(new StatusCheckRepository(db),
            new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db),
            new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            factory,
            NullLogger<StatusCheckService>.Instance,
            statusCheckLinkRepository: new StatusCheckLinkRepository(db));

    private sealed class RoutingHandler(List<Recorded> requests, Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(new Recorded(request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(responder(request));
        }
    }

    private sealed class RecordingFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public List<Uri?> Requested { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this, responder));
        private sealed class Handler(RecordingFactory owner, Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requested.Add(request.RequestUri);
                await Task.Yield();
                return f(request);
            }
        }
    }
}
