using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #293 Phase C — the SLA admin surface (own SLAs tab, #302):
/// the SlaListPanel (rows + DEFAULT chip, row ⋯ overflow menu #304, set-default flow, delete verdicts
/// through the shared LinkedDeleteBlockedDialog / surfaced 422), the
/// SlaEditDialog (plain-language rows, LIVE preview through SlaDayClassifier,
/// API-mirroring validation), and the StatusApiClient SLA mappings.
/// </summary>
[TestClass]
public class SlaAdminPanelTests
{
    private const string ThreeSlasJson = """
    [
      {"id":1,"name":"Default","targetUptimePercent":100,"criticalUptimePercent":100,"slowThresholdMs":1000,"isDefault":true,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":12,"linkedCheckNames":["a","b","c","d","e","f","g","h","i","j","k","l"]}},
      {"id":2,"name":"Messenger 95","targetUptimePercent":95,"criticalUptimePercent":80,"slowThresholdMs":1500,"isDefault":false,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":3,"linkedCheckNames":["Cayoo Messenger","Public API","Web"]}},
      {"id":3,"name":"Best effort","targetUptimePercent":90,"criticalUptimePercent":50,"slowThresholdMs":5000,"isDefault":false,"createdUtc":"2026-01-01T00:00:00Z","usage":{"usedByCount":0,"linkedCheckNames":[]}}
    ]
    """;

    private const string DeleteConflictJson = """
    {"message":"SLA 'Messenger 95' is linked to 3 check(s); relink them first.","usage":{"usedByCount":3,"linkedCheckNames":["Cayoo Messenger","Public API","Web"]}}
    """;

    private const string DeleteDefaultRejectedJson = """
    {"message":"SLA 'Default' is the default; make another SLA the default first."}
    """;

    private sealed record Recorded(string Method, string Path, string? Body);

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
        => request.RequestUri!.AbsolutePath == "/admin/slas" && request.Method == HttpMethod.Get
            ? Json(ThreeSlasJson)
            : Json("{}");

    // #304: row actions live behind a per-row overflow (⋯) menu — open it first.
    private static void OpenMenu(IRenderedComponent<SlaListPanel> cut, string slaName)
        => cut.FindAll(".sla-row:not(.sla-h)").First(r => r.TextContent.Contains(slaName))
              .QuerySelector(".sla-kebab")!.Click();

    // ---- list panel ---------------------------------------------------------

    [TestMethod]
    public void ListPanel_RendersRows_DefaultChip_ActionsBehindRowMenu_NoSetDefaultOnTheDefault()
    {
        var (ctx, _) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<SlaListPanel>();

        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));
        Assert.IsTrue(cut.Markup.Contains("Messenger 95"));
        Assert.IsTrue(cut.Markup.Contains("95%"), "target column");
        Assert.IsTrue(cut.Markup.Contains("80%"), "critical column");
        Assert.IsTrue(cut.Markup.Contains("1500"), "slow ms column");
        Assert.IsTrue(cut.Markup.Contains("12 checks"));
        Assert.IsTrue(cut.Markup.Contains("0 checks"));

        // #304: the default is marked with a DEFAULT chip, not a bare star.
        var rows = cut.FindAll(".sla-row:not(.sla-h)");
        Assert.IsNotNull(rows[0].QuerySelector(".sla-default-chip"), "default row carries the DEFAULT chip");
        Assert.IsNull(rows[1].QuerySelector(".sla-default-chip"));
        Assert.AreEqual(0, cut.FindAll(".sla-star").Count, "the bare star is gone");

        // #304: actions are not in the data row — they appear only after opening
        // the row's ⋯ menu.
        Assert.AreEqual(0, cut.FindAll(".sla-mi").Count, "no action items rendered until a menu is opened");

        OpenMenu(cut, "Messenger 95");
        var items = cut.FindAll(".sla-mi").Select(b => b.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Edit", "Set default", "Delete" }, items, "non-default row offers all three");

        OpenMenu(cut, "Default");   // switches the open menu to the default row
        var defItems = cut.FindAll(".sla-mi").Select(b => b.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Edit", "Delete" }, defItems, "the default row offers no Set default");

        Assert.IsTrue(cut.Markup.Contains("assigned to new checks"), "the explanatory footnote");
    }

    [TestMethod]
    public void SetDefault_PatchesTheApi_AndReloads()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Patch && request.RequestUri!.AbsolutePath == "/admin/slas/2/default"
                ? Json("""{"id":2,"isDefault":true}""")
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var cut = ctx.RenderComponent<SlaListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));

        OpenMenu(cut, "Messenger 95");
        cut.FindAll(".sla-mi").First(b => b.TextContent.Trim() == "Set default").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(requests.Any(r => r is { Method: "PATCH", Path: "/admin/slas/2/default" }));
            Assert.IsTrue(requests.Count(r => r is { Method: "GET", Path: "/admin/slas" }) >= 2, "panel reloads after the switch");
        });
    }

    [TestMethod]
    public void Delete_LinkedSla_RendersSharedBlockedDialog_FromThe409Payload()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/admin/slas/2"
                ? Json(DeleteConflictJson, HttpStatusCode.Conflict)
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<SlaListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));

        // Messenger 95 is linked → straight to the API; the 409
        // LinkedEntitySummary drives the SHARED blocked dialog.
        OpenMenu(cut, "Messenger 95");
        cut.FindAll(".sla-mi.danger").Single().Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("DELETE BLOCKED"));
            Assert.IsTrue(provider.Markup.Contains("Cayoo Messenger"), "linked check names listed");
            Assert.IsTrue(provider.Markup.Contains("Public API"));
        });
        Assert.IsTrue(requests.Any(r => r is { Method: "DELETE", Path: "/admin/slas/2" }));

        var actions = provider.FindAll(".mud-dialog-actions button");
        Assert.AreEqual(1, actions.Count, "Cancel is the only offered action");
        Assert.AreEqual("Cancel", actions[0].TextContent.Trim());
    }

    [TestMethod]
    public void Delete_DefaultSla_SurfacesThe422Message()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/admin/slas/1"
                ? Json(DeleteDefaultRejectedJson, HttpStatusCode.UnprocessableEntity)
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<SlaListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));

        OpenMenu(cut, "Default");
        cut.FindAll(".sla-mi.danger").Single().Click();

        cut.WaitForAssertion(() => Assert.IsTrue(
            requests.Any(r => r is { Method: "DELETE", Path: "/admin/slas/1" }),
            "the default goes straight to the API (no destructive confirm), which rejects it"));
        Assert.IsFalse(provider.Markup.Contains("DELETE BLOCKED"), "422 is not the 409 dialog");
        var snackbar = ctx.Services.GetRequiredService<ISnackbar>();
        cut.WaitForAssertion(() => Assert.IsTrue(
            snackbar.ShownSnackbars.Any(s => s.Message?.ToString()?.Contains("make another SLA the default first") == true),
            "the API's 422 message is surfaced verbatim"));
    }

    [TestMethod]
    public void Delete_UnlinkedNonDefault_ConfirmsThenDeletes()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/admin/slas/3"
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<SlaListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));

        OpenMenu(cut, "Best effort");
        cut.FindAll(".sla-mi.danger").Single().Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("Delete Best effort?")));
        Assert.IsFalse(requests.Any(r => r.Method == "DELETE"), "nothing deleted before the confirm");

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Delete").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "DELETE", Path: "/admin/slas/3" })));
    }

    // ---- edit dialog --------------------------------------------------------

    [TestMethod]
    public void EditDialog_PlainLanguageRows_AndLivePreview_RelaxedSlaIsGreen()
    {
        var (ctx, _) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<SlaListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));

        // Edit the relaxed 95/80 SLA — the fixed example day (2 down + 1 slow
        // of 387 ticks: availability 99.5, health 99.2) classifies GREEN.
        OpenMenu(cut, "Messenger 95");
        cut.FindAll(".sla-mi").First(b => b.TextContent.Trim() == "Edit").Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("EDIT SLA"));
            Assert.IsTrue(provider.Markup.Contains("green"), "plain-language green row");
            Assert.IsTrue(provider.Markup.Contains("% uptime"));
            Assert.IsTrue(provider.Markup.Contains("% availability"));
            Assert.IsTrue(provider.Markup.Contains("Slow tick above"));
            Assert.IsTrue(provider.Markup.Contains("between the two thresholds the day renders orange"));
            Assert.IsTrue(provider.Markup.Contains("affects new ticks only"), "slow-threshold helper");
            Assert.IsTrue(provider.Markup.Contains("Preview: a day with 2 down + 1 slow of 387 ticks"));
            StringAssert.Contains(provider.Find(".sla-preview").TextContent, "GREEN",
                "availability 99.5 ≥ 80 and health 99.2 ≥ 95 → GREEN");
            Assert.IsTrue(provider.Markup.Contains("Linked checks: Cayoo Messenger, Public API, Web"));
        });
    }

    [TestMethod]
    public void EditDialog_LivePreview_StrictDefaultIsRed_AndRecomputesOnInput()
    {
        var (ctx, _) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<SlaListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));

        // The strict 100/100 Default: 2 down → availability 99.5 < 100 → RED.
        OpenMenu(cut, "Default");
        cut.FindAll(".sla-mi").First(b => b.TextContent.Trim() == "Edit").Click();
        provider.WaitForAssertion(() => StringAssert.Contains(provider.Find(".sla-preview").TextContent, "RED"));

        // Relax the red threshold below the example's availability → the down
        // verdict clears, but health 99.2 < 100 (the green bar) → AMBER.
        provider.Find(".sla-critical input").Input("80");
        provider.WaitForAssertion(() => StringAssert.Contains(provider.Find(".sla-preview").TextContent, "AMBER",
            "the preview reclassifies on input through SlaDayClassifier"));

        // Relax the green bar too → GREEN.
        provider.Find(".sla-target input").Input("95");
        provider.WaitForAssertion(() => StringAssert.Contains(provider.Find(".sla-preview").TextContent, "GREEN"));
    }

    [TestMethod]
    public void EditDialog_CriticalAboveTarget_BlocksSave_NamesTheRule_NoPost()
    {
        var (ctx, requests) = Ctx(DefaultRoutes);
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<SlaListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));

        OpenMenu(cut, "Messenger 95");
        cut.FindAll(".sla-mi").First(b => b.TextContent.Trim() == "Edit").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("EDIT SLA")));

        provider.Find(".sla-critical input").Input("99.5");   // above the 95 target
        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() => Assert.IsTrue(
            provider.Markup.Contains("The red threshold can't be above the green threshold."),
            "mirrors the API's Critical ≤ Target rule"));
        Assert.IsFalse(requests.Any(r => r.Method == "POST"), "invalid form never posts");
    }

    [TestMethod]
    public void EditDialog_Save_PostsToAdminSlas_AndCloses()
    {
        var (ctx, requests) = Ctx(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/admin/slas"
                ? Json("""{"id":2,"name":"Messenger 95","targetUptimePercent":95,"criticalUptimePercent":80,"slowThresholdMs":1500,"isDefault":false,"usage":{"usedByCount":3,"linkedCheckNames":[]}}""")
                : DefaultRoutes(request));
        using var _ctx = ctx;
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<SlaListPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(3, cut.FindAll(".sla-row:not(.sla-h)").Count));

        OpenMenu(cut, "Messenger 95");
        cut.FindAll(".sla-mi").First(b => b.TextContent.Trim() == "Edit").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("EDIT SLA")));

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/admin/slas" }), "saves via POST /admin/slas");
            Assert.IsFalse(provider.Markup.Contains("EDIT SLA"), "dialog closed after save");
        });
    }

    // ---- API client mappings ------------------------------------------------

    [TestMethod]
    public async Task Client_SetDefault_PatchTrue_404False()
    {
        var requests = new List<Recorded>();
        var ok = new StatusApiClient(new HttpClient(new RoutingHandler(requests,
            _ => Json("""{"id":2,"isDefault":true}""")))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue(await ok.SetDefaultSlaAsync(2));
        Assert.IsTrue(requests.Any(r => r is { Method: "PATCH", Path: "/admin/slas/2/default" }));

        var missing = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsFalse(await missing.SetDefaultSlaAsync(99));
    }

    [TestMethod]
    public async Task Client_Delete_Maps204_404_409_And422Throws()
    {
        var deleted = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue((await deleted.DeleteSlaAsync(3)).Deleted);

        var missing = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue((await missing.DeleteSlaAsync(9)).NotFound);

        var blocked = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => Json(DeleteConflictJson, HttpStatusCode.Conflict)))
        { BaseAddress = new Uri("http://api.test") });
        var conflict = await blocked.DeleteSlaAsync(2);
        Assert.IsNotNull(conflict.BlockedBy);
        Assert.AreEqual(3, conflict.BlockedBy.UsedByCount);
        CollectionAssert.AreEqual(new[] { "Cayoo Messenger", "Public API", "Web" }, conflict.BlockedBy.LinkedCheckNames);

        var rejected = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => Json(DeleteDefaultRejectedJson, HttpStatusCode.UnprocessableEntity)))
        { BaseAddress = new Uri("http://api.test") });
        var ex = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => rejected.DeleteSlaAsync(1));
        StringAssert.Contains(ex.Message, "make another SLA the default first");
    }

    [TestMethod]
    public async Task Client_GetAndSave_ParseTheViewModel()
    {
        var list = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(), _ => Json(ThreeSlasJson)))
        { BaseAddress = new Uri("http://api.test") });
        var slas = await list.GetSlasAsync();
        Assert.AreEqual(3, slas.Count);
        Assert.IsTrue(slas[0].IsDefault);
        Assert.AreEqual(12, slas[0].Usage.UsedByCount);
        Assert.AreEqual(1500, slas[1].SlowThresholdMs);

        var save = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => Json("""{"id":7,"name":"Gold","targetUptimePercent":99.9,"criticalUptimePercent":99,"slowThresholdMs":250,"isDefault":false,"usage":{"usedByCount":0,"linkedCheckNames":[]}}""")))
        { BaseAddress = new Uri("http://api.test") });
        var saved = await save.SaveSlaAsync(new SlaViewModel { Name = "Gold" });
        Assert.AreEqual(7, saved.Id, "the echo carries the assigned id (quick-create auto-select)");

        var invalid = new StatusApiClient(new HttpClient(new RoutingHandler(new List<Recorded>(),
            _ => Json("""{"message":"criticalUptimePercent must be less than or equal to targetUptimePercent"}""", HttpStatusCode.UnprocessableEntity)))
        { BaseAddress = new Uri("http://api.test") });
        var ex = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => invalid.SaveSlaAsync(new SlaViewModel { Name = "x" }));
        StringAssert.Contains(ex.Message, "criticalUptimePercent");
    }

    // ---- fixtures -----------------------------------------------------------

    private sealed class RoutingHandler(List<Recorded> requests, Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add(new Recorded(request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return responder(request);
        }
    }
}
