using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #335 — the Plugins page: a read-only catalogue of the registered check
/// providers rendered entirely from GET /statuscheck/providers plus a client-side
/// usage count over /statuscheck. Covers: the happy-path list (rows, counts,
/// metric chips, push tag), the calm empty/failed-fetch states, honest usage
/// degradation, the secret-labels-only invariant, unknown-icon fallback, the
/// unknown-stored-type orphan row, and the New-check dialog preselect.
/// #343 Phase 2: also the "Notification channels" group from GET
/// /notifications/providers — channel rows, the SupportsTest capability tag, and
/// the calm channel-fetch-failure state. Check-panel assertions are scoped to
/// <c>.plugins-frame .prov</c> now that the page has two catalogue sections.
/// </summary>
[TestClass]
public class PluginsPageTests
{
    /// <summary>The three real providers, shaped like CheckProviderApi.ToViewModel
    /// serializes them (#312/#317/#320 + the #335 description/direction fields).</summary>
    private const string ProvidersJson = """
    [
      {"typeId":"http","displayName":"HTTP(S)","icon":"link","schemaVersion":1,
       "description":"Pull probe — GETs a URL each tick and asserts the expected status code and response time.",
       "direction":"pull",
       "fields":[
         {"key":"url","label":"URL","kind":"text","required":true,"options":[]},
         {"key":"expectedStatusCode","label":"Expected status","kind":"number","required":true,"options":[]}],
       "metrics":[]},
      {"typeId":"ai","displayName":"AI / LLM endpoint","icon":"sparkle","schemaVersion":1,
       "description":"Pull probe — streams a canary prompt to an OpenAI-compatible endpoint; asserts content, TTFT and throughput.",
       "direction":"pull",
       "fields":[
         {"key":"baseUrl","label":"Base URL","kind":"text","required":true,"options":[]},
         {"key":"model","label":"Model","kind":"text","required":true,"options":[]},
         {"key":"apiKey","label":"API key","kind":"secret","required":false,"options":[]}],
       "metrics":[
         {"key":"ttft_ms","label":"TTFT","unit":"ms","kind":"gauge"},
         {"key":"tokens_per_sec","label":"Throughput","unit":"tok/s","kind":"gauge"}]},
      {"typeId":"heartbeat","displayName":"Agent heartbeat","icon":"pulse","schemaVersion":1,
       "description":"Push / dead-man's-switch — the agent pings a token URL each run; the check goes down when a ping is overdue.",
       "direction":"push",
       "fields":[
         {"key":"intervalSeconds","label":"Expected interval (s)","kind":"number","required":true,"options":[]},
         {"key":"graceSeconds","label":"Grace (s)","kind":"number","required":true,"options":[]}],
       "metrics":[
         {"key":"seconds_since_heartbeat","label":"Since heartbeat","unit":"s","kind":"gauge"}]}
    ]
    """;

    /// <summary>2× http, 1× ai, 0× heartbeat.</summary>
    private const string ChecksJson = """
    {"results":[
       {"id":1,"title":"Public API","providerType":"http"},
       {"id":2,"title":"Landing","providerType":"http"},
       {"id":3,"title":"Inference","providerType":"ai"}],
     "currentPage":1,"pageCount":1,"pageSize":50,"rowCount":3}
    """;

    /// <summary>The two real channels, shaped like NotificationProviderApi.ToViewModel
    /// serializes them (#343 Phase 1/2). Email supports a test send; web push does not.</summary>
    private const string ChannelsJson = """
    [
      {"typeId":"email","displayName":"Email (SMTP)","icon":"mail",
       "description":"Sends alert emails through the operator-configured SMTP relay.",
       "supportsTest":true,"category":"notification"},
      {"typeId":"webpush","displayName":"Browser push","icon":"bell",
       "description":"Pushes alerts to subscribed browsers via Web Push (VAPID).",
       "supportsTest":false,"category":"notification"}
    ]
    """;

    private sealed record Recorded(string Method, string Path);

    private static (BunitTestContext ctx, List<Recorded> requests) Ctx(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var ctx = new BunitTestContext();
        var requests = new List<Recorded>();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new RoutingHandler(requests, responder ?? DefaultRoutes)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return (ctx, requests);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage DefaultRoutes(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/statuscheck/providers" => Json(ProvidersJson),
        "/notifications/providers" => Json(ChannelsJson),
        "/statuscheck" => Json(ChecksJson),
        // Routes the edit dialog needs when opened from the page.
        "/admin/slas" => Json("[]"),
        "/admin/webhooks" => Json("[]"),
        "/admin/alert-profiles" => Json("[]"),
        _ => Json("{}"),
    };

    // ---- happy path ----------------------------------------------------------

    [TestMethod]
    public void RendersAllProviders_WithCounts_MetricChips_AndPushTag()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() =>
        {
            var rows = page.FindAll(".plugins-frame .prov");
            Assert.AreEqual(3, rows.Count, "one row per registered provider, no orphans");
        });

        // Names + descriptions come from the descriptor, never page-local prose.
        StringAssert.Contains(page.Markup, "HTTP(S)");
        StringAssert.Contains(page.Markup, "AI / LLM endpoint");
        StringAssert.Contains(page.Markup, "Agent heartbeat");
        StringAssert.Contains(page.Markup, "dead-man");

        // Usage counts: 2× http, 1× ai, 0× heartbeat.
        var used = page.FindAll(".plugins-frame .prov-used").Select(e => e.TextContent.Trim()).ToList();
        Assert.AreEqual(3, used.Count);
        StringAssert.Contains(used[0], "2");
        StringAssert.Contains(used[1], "1");
        StringAssert.Contains(used[2], "0");

        // Metric chips render declared defs; http gets the honest empty chip.
        StringAssert.Contains(page.Markup, "ttft_ms");
        StringAssert.Contains(page.Markup, "seconds_since_heartbeat");
        Assert.AreEqual(1, page.FindAll(".mchip.none").Count, "exactly one 'no declared metrics' chip (http)");

        // PUSH tag only on the push-direction provider.
        var pushTags = page.FindAll(".tag.push");
        Assert.AreEqual(1, pushTags.Count);
        Assert.IsTrue(page.Find("[data-provider=heartbeat]").TextContent.Contains("PUSH"));
        Assert.IsFalse(page.Find("[data-provider=http]").TextContent.Contains("PUSH"));

        // The Phase-4 roadmap panel is present and explicitly "planned".
        StringAssert.Contains(page.Markup, "PLANNED — PHASE 4");
    }

    // ---- empty / failure states ----------------------------------------------

    [TestMethod]
    public void ProviderFetchFailure_RendersCalmEmptyState()
    {
        // GetCheckProvidersAsync swallows transport errors into an empty list —
        // the page must state that plainly instead of rendering a broken frame.
        var (ctx, _) = Ctx(req => req.RequestUri!.AbsolutePath == "/statuscheck/providers"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : DefaultRoutes(req));
        using var _ctx = ctx;
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "No check providers reported"));
        Assert.AreEqual(0, page.FindAll(".plugins-frame .prov").Count);
    }

    [TestMethod]
    public void ChecksFetchFailure_DegradesUsageHonestly_ProvidersStillRender()
    {
        var (ctx, _) = Ctx(req => req.RequestUri!.AbsolutePath == "/statuscheck"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : DefaultRoutes(req));
        using var _ctx = ctx;
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() => Assert.AreEqual(3, page.FindAll(".plugins-frame .prov").Count));
        StringAssert.Contains(page.Markup, "usage unavailable");
        Assert.IsFalse(page.Markup.Contains("Used by"), "no fabricated counts when the checks fetch failed");
    }

    // ---- invariants ------------------------------------------------------------

    [TestMethod]
    public void SecretFields_RenderAsLabelsOnly_NoInputsOnThePage()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() => Assert.AreEqual(3, page.FindAll(".plugins-frame .prov").Count));

        // The ai provider's secret is named with its label + write-only rule…
        StringAssert.Contains(page.Markup, "API key");
        StringAssert.Contains(page.Markup, "write-only");
        // …and the catalogue is read-only: no form controls anywhere, so there is
        // no surface that could echo a stored value (#312 secret rule).
        Assert.AreEqual(0, page.FindAll("input, textarea, select").Count);
    }

    [TestMethod]
    public void UnknownIcon_FallsBackToNeutralGlyph()
    {
        var (ctx, _) = Ctx(req => req.RequestUri!.AbsolutePath switch
        {
            "/statuscheck/providers" => Json("""[{"typeId":"tcp","displayName":"TCP port","icon":"wat","schemaVersion":1,"description":"","direction":"pull","fields":[],"metrics":[]}]"""),
            "/statuscheck" => Json("""{"results":[],"currentPage":1,"pageCount":1,"pageSize":50,"rowCount":0}"""),
            _ => DefaultRoutes(req),
        });
        using var _ctx = ctx;
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() => Assert.AreEqual(1, page.FindAll(".plugins-frame .prov").Count));
        Assert.AreEqual("◈", page.Find(".plugins-frame .prov-icon").TextContent.Trim());
    }

    [TestMethod]
    public void UnknownStoredProviderType_SurfacesAsDisabledOrphanRow()
    {
        var (ctx, _) = Ctx(req => req.RequestUri!.AbsolutePath == "/statuscheck"
            ? Json("""{"results":[{"id":9,"title":"Legacy","providerType":"tcp"}],"currentPage":1,"pageCount":1,"pageSize":50,"rowCount":1}""")
            : DefaultRoutes(req));
        using var _ctx = ctx;
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "Unknown type — tcp"));
        StringAssert.Contains(page.Markup, "NOT REGISTERED");
        StringAssert.Contains(page.Markup, "will not run");
        // The orphan row is informational only — no New-check button on it.
        Assert.AreEqual(0, page.Find(".prov.orphan").QuerySelectorAll("button").Length);
    }

    // ---- New check CTA ----------------------------------------------------------

    [TestMethod]
    public void NewCheck_OpensEditDialog_WithProviderTypePreselected()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        ctx.RenderComponent<MudPopoverProvider>();
        var dialogHost = ctx.RenderComponent<MudDialogProvider>();
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() => Assert.AreEqual(3, page.FindAll(".plugins-frame .prov").Count));
        page.Find("[data-provider=heartbeat] button").Click();

        // The dialog opens on the heartbeat type: its schema-driven config fields
        // (not http's url field) are the generated form.
        dialogHost.WaitForAssertion(() =>
        {
            StringAssert.Contains(dialogHost.Markup, "chk-provider-type");
            StringAssert.Contains(dialogHost.Markup, "chk-cfg-intervalSeconds");
        });
        Assert.IsFalse(dialogHost.Markup.Contains("chk-cfg-url"), "http fields must not render for a heartbeat preselect");
    }

    // ---- notification channels (#343 Phase 2) --------------------------------

    [TestMethod]
    public void RendersNotificationChannels_Grouped_WithTestCapabilityTag()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() =>
            Assert.AreEqual(2, page.FindAll(".plugins-channels .prov").Count, "one row per registered channel"));

        // Section heading + both channels, rendered off the descriptor (no page-local prose).
        StringAssert.Contains(page.Markup, "Notification channels");
        StringAssert.Contains(page.Markup, "Email (SMTP)");
        StringAssert.Contains(page.Markup, "Browser push");
        StringAssert.Contains(page.Markup, "Web Push (VAPID)");

        // The test-capability tag is gated on SupportsTest: email yes, web push no.
        Assert.IsTrue(page.Find("[data-channel=email]").TextContent.Contains("TEST"));
        Assert.IsFalse(page.Find("[data-channel=webpush]").TextContent.Contains("TEST"));
        Assert.AreEqual(1, page.FindAll(".plugins-channels .tag.test").Count, "exactly one TEST tag (email)");
    }

    [TestMethod]
    public void ChannelFetchFailure_RendersCalmEmptyState_ChecksStillRender()
    {
        // GetNotificationProvidersAsync swallows transport errors into an empty list —
        // the channels panel must state that plainly; the check catalogue is independent.
        var (ctx, _) = Ctx(req => req.RequestUri!.AbsolutePath == "/notifications/providers"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : DefaultRoutes(req));
        using var _ctx = ctx;
        var page = ctx.RenderComponent<Plugins>();

        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "No notification channels reported"));
        Assert.AreEqual(0, page.FindAll(".plugins-channels .prov").Count);
        Assert.AreEqual(3, page.FindAll(".plugins-frame .prov").Count, "the check catalogue renders independently");
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
