using System.Net;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using SuperStatus.Web.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #291 Phase C — the Alert Profiles surface on the Alerts tab: the
/// AlertProfileListPanel (rows + used-by counts + the site-default flag, empty
/// state, delete guard via the SHARED LinkedDeleteBlockedDialog), the edit
/// dialog validation rules (incl. the mutually-exclusive site-default toggle),
/// the API client mappings, the profile-name resolution on the alert log, and
/// the Alerts-pane ordering (profiles panel first). Mirrors
/// <see cref="WebhookAdminPhaseBTests"/>.
/// </summary>
[TestClass]
public class AlertProfileAdminPhaseCTests
{
    // ---- stubbed /admin/alert-profiles feeds --------------------------------

    private const string ThreeProfilesJson = """
    [
      {"id":1,"name":"On-call email","emailEnabled":true,"emailRecipients":"noc@ex.com,ops@ex.com","usesSiteDefaultRecipients":false,"webPushEnabled":false,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":5,"linkedCheckNames":["Public API","Web","DB","Mail","CDN"]}},
      {"id":2,"name":"Push only","emailEnabled":false,"emailRecipients":"","usesSiteDefaultRecipients":false,"webPushEnabled":true,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":2,"linkedCheckNames":["Web","DB"]}},
      {"id":3,"name":"Default recipients","emailEnabled":true,"emailRecipients":"","usesSiteDefaultRecipients":true,"webPushEnabled":false,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":0,"linkedCheckNames":[]}}
    ]
    """;

    private const string DeleteConflictJson = """
    {"message":"Alert profile 'On-call email' is linked to 5 check(s); unlink it first.","usage":{"usedByCount":5,"linkedCheckNames":["Public API","Web","DB","Mail","CDN"]}}
    """;

    private const string WebhookProfileJson = """
    [
      {"id":4,"name":"Webhook profile","emailEnabled":false,"emailRecipients":"","usesSiteDefaultRecipients":false,"webPushEnabled":false,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":1,"linkedCheckNames":["Public API"]},
       "channels":[{"providerType":"webhook","isEnabled":true,"config":{"url":"https://hook.example/fire","payloadJson":"{\"service\":\"{check}\"}"},"storedSecretKeys":[]}]}
    ]
    """;

    private const string WebhookProviderJson = """
    [
      {"typeId":"webhook","displayName":"Webhook","icon":"webhook",
       "description":"POSTs a JSON alert payload to an operator-configured URL.",
       "supportsTest":true,"category":"notification",
       "fields":[
         {"key":"url","label":"Webhook URL","kind":"text","required":true,"help":"The endpoint the JSON alert payload is POSTed to.","placeholder":"https://example.com/hooks/alerts","options":[]},
         {"key":"payloadJson","label":"Payload JSON","kind":"json","required":false,"help":"Optional JSON body template.","placeholder":"{}","options":[]}
       ]}
    ]
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
        => request.RequestUri!.AbsolutePath == "/admin/alert-profiles" && request.Method == HttpMethod.Get
            ? Json(ThreeProfilesJson)
            : Json("{}");

    // ---- list panel ---------------------------------------------------------

    [TestMethod]
    public void ListPanel_RendersRows_WithUsedByCounts_PushMark_AndSiteDefaultFlag()
    {
        var (ctx, _) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<AlertProfileListPanel>();

        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".ap-row:not(.ap-h)").Count));
        Assert.IsTrue(cut.Markup.Contains("On-call email"));
        Assert.IsTrue(cut.Markup.Contains("noc@ex.com, ops@ex.com"), "recipients render with the mockup's comma+space spacing");
        Assert.IsTrue(cut.Markup.Contains("5 checks"), "used-by count from the embedded LinkedEntitySummary");
        Assert.IsTrue(cut.Markup.Contains("2 checks"));
        Assert.IsTrue(cut.Markup.Contains("0 checks"), "unlinked profile shows zero");

        // The default-recipient profile renders the Email plugin default marker, not a recipients list.
        Assert.IsTrue(cut.Markup.Contains("(Email plugin default)"));
        var flag = cut.Find(".ap-flag");
        Assert.AreEqual("EMAIL DEFAULT", flag.TextContent.Trim());
        Assert.IsTrue(flag.ClassList.Contains("tag") && flag.ClassList.Contains("accent"), "small accent tag per the mockup");

        // Push column: ✓ only for the web-push profile.
        var pushOn = cut.FindAll(".ap-push.on");
        Assert.AreEqual(1, pushOn.Count);
        Assert.AreEqual("✓", pushOn[0].TextContent.Trim());

        Assert.IsTrue(cut.FindAll("button").Any(b => b.TextContent.Trim() == "+ Add profile"), "header add button");
    }

    [TestMethod]
    public void ListPanel_EmptyState_ShowsExactCopy_AndAddButton()
    {
        var (ctx, _) = Ctx(_ => Json("[]"));
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<AlertProfileListPanel>();

        cut.WaitForAssertion(() => Assert.IsTrue(
            cut.Markup.Contains("No alert profiles yet — create one to define who gets notified, then link it from any check."),
            "the deliberate empty-state copy, verbatim"));
        var addButtons = cut.FindAll("button").Where(b => b.TextContent.Trim() == "+ Add profile").ToList();
        Assert.AreEqual(1, addButtons.Count, "empty state carries its own add button");
    }

    // ---- delete guard (the SHARED dialog) -----------------------------------

    [TestMethod]
    public void Delete_LinkedProfile_RendersSharedBlockedDialog_FromThe409Payload_CancelOnly()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/admin/alert-profiles/1"
                ? Json(DeleteConflictJson, HttpStatusCode.Conflict)
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<AlertProfileListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".ap-row:not(.ap-h)").Count));

        // Row 1 is linked (5 checks) — Delete goes to the API and the 409
        // LinkedEntitySummary payload drives the blocked dialog.
        cut.FindAll(".ap-link.danger")[0].Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("DELETE BLOCKED"));
            Assert.IsTrue(provider.Markup.Contains("Public API"), "linked check names listed");
            Assert.IsTrue(provider.Markup.Contains("CDN"), "all linked check names listed");
            Assert.IsTrue(provider.Markup.Contains("alert profile"), "entity label woven into the copy");
        });
        Assert.IsTrue(requests.Any(r => r is { Method: "DELETE", Path: "/admin/alert-profiles/1" }));

        // It is the ONE shared component (also used by the Webhooks tab), not a copy.
        Assert.AreEqual(1, provider.FindComponents<LinkedDeleteBlockedDialog>().Count,
            "the 409 guard renders through the shared LinkedDeleteBlockedDialog");

        // The only offered action is Cancel — no confirm/unlink-all in this phase.
        var actions = provider.FindAll(".mud-dialog-actions button");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("Cancel", actions[0].TextContent.Trim());
    }

    [TestMethod]
    public void Delete_UnlinkedProfile_ConfirmsThenDeletes()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/admin/alert-profiles/3"
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<AlertProfileListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".ap-row:not(.ap-h)").Count));

        // Row 3 is unlinked — a plain destructive confirm precedes the DELETE.
        cut.FindAll(".ap-link.danger")[2].Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("Delete Default recipients?")));
        Assert.IsFalse(requests.Any(r => r.Method == "DELETE"), "nothing deleted before the confirm");

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Delete").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(
            requests.Any(r => r is { Method: "DELETE", Path: "/admin/alert-profiles/3" })));
    }

    // ---- edit dialog --------------------------------------------------------

    [TestMethod]
    public void EditDialog_RequiresName_NoPostWhenInvalid()
    {
        var (ctx, requests) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<AlertProfileListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".ap-row:not(.ap-h)").Count));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "+ Add profile").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("ADD ALERT PROFILE")));

        // Email off → the recipients block isn't rendered at all.
        Assert.AreEqual(0, provider.FindAll(".ap-recipients").Count, "recipients field only shows when email is on");
        Assert.AreEqual(0, provider.FindAll(".ap-sitedefault").Count, "site-default toggle only shows when email is on");

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("Name is required.")));
        Assert.IsFalse(requests.Any(r => r.Method == "POST"), "invalid form never posts");
    }

    [TestMethod]
    public void EditDialog_EmailOn_NoRecipients_NoSiteDefault_BlockedClientSide_ThenSiteDefaultUnblocks()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/admin/alert-profiles"
                ? Json("""{"id":1,"name":"On-call email","emailEnabled":true,"emailRecipients":"","usesSiteDefaultRecipients":true,"webPushEnabled":false,"usage":{"usedByCount":5,"linkedCheckNames":["Public API","Web","DB","Mail","CDN"]}}""")
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<AlertProfileListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".ap-row:not(.ap-h)").Count));

        // Edit "On-call email" (email on, explicit recipients) and blank the recipients.
        cut.FindAll(".ap-link").First(b => b.TextContent.Trim() == "Edit").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("EDIT ALERT PROFILE")));
        provider.Find(".ap-recipients input").Change("");

        // Email on + no recipients + no default fallback → blocked with a visible
        // message (mirrors the API's 422 rule), nothing posted.
        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains(
            "Email is enabled but the profile has no recipients — add recipients or turn on Email plugin default recipients.")));
        Assert.IsFalse(requests.Any(r => r.Method == "POST"), "the invalid combination never posts");

        // Turning the Email plugin default toggle on resolves it — Save now posts and closes.
        provider.Find(".ap-sitedefault input").Change(true);
        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/admin/alert-profiles" }), "saves via POST /admin/alert-profiles");
            Assert.IsFalse(provider.Markup.Contains("EDIT ALERT PROFILE"), "dialog closed after save");
        });
    }

    [TestMethod]
    public void EditDialog_SiteDefaultToggle_DisablesAndClearsRecipients()
    {
        var (ctx, _) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<AlertProfileListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".ap-row:not(.ap-h)").Count));

        cut.FindAll(".ap-link").First(b => b.TextContent.Trim() == "Edit").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("EDIT ALERT PROFILE")));

        // Before: explicit recipients, field editable.
        var recipients = provider.Find(".ap-recipients input");
        Assert.IsFalse(recipients.HasAttribute("disabled"));
        Assert.AreEqual("noc@ex.com,ops@ex.com", recipients.GetAttribute("value"));
        Assert.IsTrue(provider.Markup.Contains("Comma/space-separated."), "recipients helper text");

        Assert.IsTrue(provider.Markup.Contains("Use Email plugin default recipients"));
        Assert.IsTrue(provider.Markup.Contains("Plugins -&gt; Email -&gt; Configure")
            || provider.Markup.Contains("Plugins -> Email -> Configure"));

        // Mutually exclusive UX: default fallback on → recipients disabled AND cleared.
        provider.Find(".ap-sitedefault input").Change(true);

        provider.WaitForAssertion(() =>
        {
            var input = provider.Find(".ap-recipients input");
            Assert.IsTrue(input.HasAttribute("disabled"), "recipients field disabled while default fallback is on");
            Assert.IsTrue(string.IsNullOrEmpty(input.GetAttribute("value")), "recipients cleared, never a stale copy");
        });
    }

    [TestMethod]
    public void EditDialog_WebhookChannel_RendersPayloadJsonTextarea()
    {
        var (ctx, _) = Ctx(request => request.RequestUri!.AbsolutePath switch
        {
            "/admin/alert-profiles"     => Json(WebhookProfileJson),
            "/notifications/providers"  => Json(WebhookProviderJson),
            _                           => Json("{}"),
        });
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<AlertProfileListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(1, cut.FindAll(".ap-row:not(.ap-h)").Count));

        cut.FindAll(".ap-link").First(b => b.TextContent.Trim() == "Edit").Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("Webhook"));
            Assert.IsTrue(provider.Markup.Contains("Payload JSON"));
            Assert.AreEqual(1, provider.FindAll(".ap-cfg-webhook-payloadJson textarea").Count,
                "webhook payload JSON config renders as a multiline textarea");
        });
    }

    [TestMethod]
    public void EditDialog_ExistingProfile_ShowsReadOnlyLinkedChecks_AndPostsOnSave()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/admin/alert-profiles"
                ? Json("""{"id":1,"name":"On-call email","emailEnabled":true,"emailRecipients":"noc@ex.com,ops@ex.com","usesSiteDefaultRecipients":false,"webPushEnabled":false,"usage":{"usedByCount":5,"linkedCheckNames":["Public API","Web","DB","Mail","CDN"]}}""")
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<AlertProfileListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".ap-row:not(.ap-h)").Count));

        cut.FindAll(".ap-link").First(b => b.TextContent.Trim() == "Edit").Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("EDIT ALERT PROFILE"));
            Assert.IsTrue(provider.Markup.Contains("Linked checks: Public API, Web, DB, Mail, CDN"),
                "read-only linked-checks line when editing an existing profile");
        });

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/admin/alert-profiles" }), "saves via POST /admin/alert-profiles");
            Assert.IsFalse(provider.Markup.Contains("EDIT ALERT PROFILE"), "dialog closed after save");
        });
    }

    // ---- client mappings ----------------------------------------------------

    [TestMethod]
    public async Task Client_DeleteProfile_Maps204_404_And409WithUsage()
    {
        var deleted = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue((await deleted.DeleteAlertProfileAsync(1)).Deleted);

        var missing = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue((await missing.DeleteAlertProfileAsync(2)).NotFound);

        var blocked = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => Json(DeleteConflictJson, HttpStatusCode.Conflict)))
        { BaseAddress = new Uri("http://api.test") });
        var conflict = await blocked.DeleteAlertProfileAsync(3);
        Assert.IsFalse(conflict.Deleted);
        Assert.IsNotNull(conflict.BlockedBy);
        Assert.AreEqual(5, conflict.BlockedBy.UsedByCount);
        CollectionAssert.AreEqual(new[] { "Public API", "Web", "DB", "Mail", "CDN" }, conflict.BlockedBy.LinkedCheckNames);
    }

    // ---- profile name on the alert log --------------------------------------

    [TestMethod]
    public async Task AlertLog_ResolvesProfileName_ViaTheRepositoryJoin_NoSchemaChange()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = new StatusCheck { Title = "acme", StatusCheckUrl = "x", ServiceLogoUrl = "" };
        var profile = new AlertProfile { Name = "On-call email", EmailEnabled = true, EmailRecipients = "ops@ex.com", CreatedUtc = DateTime.UtcNow };
        db.StatusCheckSet.Add(check);
        db.AlertProfileSet.Add(profile);
        await db.SaveChangesAsync();
        db.AlertDeliveryLogSet.AddRange(
            new AlertDeliveryLog { StatusCheckId = check.Id, AlertProfileId = profile.Id, AttemptedUtc = DateTime.UtcNow, ChannelTypeId = NotificationChannelTypes.Email, Trigger = AlertTrigger.Outage, Outcome = AlertOutcome.Fired },
            // pre-#291 row: no AlertProfileId → name resolves to null, UI shows "—".
            new AlertDeliveryLog { StatusCheckId = check.Id, AttemptedUtc = DateTime.UtcNow.AddMinutes(-1), ChannelTypeId = NotificationChannelTypes.Email, Trigger = AlertTrigger.Outage, Outcome = AlertOutcome.Fired });
        await db.SaveChangesAsync();

        var rows = await new AlertDeliveryLogRepository(db).GetRecentWithCheckAsync(100, failuresOnly: false);
        var vms = rows.Select(r => new AlertDeliveryLogViewModel(r)).ToList();

        Assert.AreEqual(2, vms.Count);
        Assert.AreEqual("On-call email", vms[0].AlertProfileName, "linked profile's name is joined in the repository read");
        Assert.AreEqual(profile.Id, vms[0].AlertProfileId);
        Assert.IsNull(vms[1].AlertProfileName, "pre-#291 rows have no profile name");
        Assert.IsNull(vms[1].AlertProfileId);
    }

    [TestMethod]
    public void AlertLogPanel_RendersProfileNameColumn()
    {
        var (ctx, _) = Ctx(request => request.RequestUri!.AbsolutePath == "/admin/alert-log"
            ? Json("""
              [
                {"id":1,"statusCheckId":1,"checkTitle":"acme","alertProfileId":7,"alertProfileName":"On-call email","attemptedUtc":"2026-06-01T10:00:00Z","channel":0,"trigger":0,"target":"ops@ex.com","outcome":0},
                {"id":2,"statusCheckId":1,"checkTitle":"acme","attemptedUtc":"2026-06-01T09:00:00Z","channel":0,"trigger":0,"outcome":1,"reason":"throttled"}
              ]
              """)
            : Json("{}"));
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<AlertLogPanel>();

        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll(".al-row:not(.al-h)").Count));
        var names = cut.FindAll(".al-profile").Select(e => e.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "On-call email", "—" }, names);
        Assert.IsTrue(cut.Markup.Contains("Profile"), "the log table grew a Profile column");
    }

    // ---- Alerts pane ordering ------------------------------------------------

    [TestMethod]
    public void AlertsPane_ProfilesPanelRendersFirst_AboveLog_WithoutProviderPointers()
    {
        using var ctx = AdminCtx();
        var cut = ctx.RenderComponent<Admin>();
        cut.WaitForAssertion(() => cut.Find(".console-tabs"));

        var alertsPane = cut.Find("[data-pane=\"alerts\"]").InnerHtml;
        int profiles = alertsPane.IndexOf("ALERT PROFILES", StringComparison.Ordinal);
        int log = alertsPane.IndexOf("ALERT LOG", StringComparison.Ordinal);

        Assert.IsTrue(profiles >= 0, "profiles panel lives on the Alerts pane");
        Assert.IsTrue(log > profiles, "alert log stays below alert profiles");
        Assert.IsFalse(alertsPane.Contains("Provider settings moved", StringComparison.Ordinal));
        Assert.IsFalse(alertsPane.Contains("Browser notifications moved", StringComparison.Ordinal));
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

    /// <summary>Full Admin-page context (auth + every api client), mirroring AdminTabsTests.</summary>
    private static BunitTestContext AdminCtx()
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization().SetAuthorized("operator");
        ctx.Services.AddMudServices();
        var http = new HttpClient(new AdminStub()) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        ctx.Services.AddSingleton(new SettingsApiClient(http));
        ctx.Services.AddSingleton(new IssuerModeInfo(isDynamic: false)); // #358: pinned mode ⇒ no banner/editor (markup unchanged)
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        ctx.Services.AddSingleton(new UpdatesApiClient(http));
        ctx.Services.AddSingleton(new PushApiClient(http));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private sealed class AdminStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/settings"             => """{"onboardedUtc":"2026-01-01T00:00:00Z"}""",
                "/statuscheck"          => """{"results":[],"totalCount":0,"page":1,"pageSize":50}""",
                "/admin/webhooks"       => "[]",
                "/admin/webhook-log"    => "[]",
                "/admin/alert-profiles" => "[]",
                "/admin/alert-log"      => "[]",
                "/statuscheck/summary"  => """{"services":{"up":1,"degraded":0,"down":0,"total":1},"latency_ms":{"avg":1,"p95":1},"uptime_30d_pct":100,"incidents_30d":0,"per_service":[],"overall":"up","generated_utc":"2026-01-01T00:00:00Z"}""",
                _                       => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RoutingHandler(List<Recorded> requests, Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(new Recorded(request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(responder(request));
        }
    }
}
