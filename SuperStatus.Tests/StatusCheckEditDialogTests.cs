using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.StatusCheckOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #291 Phase D — the rebuilt check edit dialog: the four mockup
/// sections, the SLA picker + webhook/profile chip pickers fed by the admin
/// endpoints, saves that post slaId + webhookIds[] + alertProfileIds[] (the
/// id-array path — no legacy embedded fields), section-naming validation, and
/// the "+ New…" quick-create flow (picker refresh + auto-select).
/// </summary>
[TestClass]
public class StatusCheckEditDialogTests
{
    private const string SlasJson = """
    [
      {"id":1,"name":"Default","targetUptimePercent":100,"criticalUptimePercent":100,"slowThresholdMs":1000,"isDefault":true,"usage":{"usedByCount":12,"linkedCheckNames":[]}},
      {"id":2,"name":"Messenger 95","targetUptimePercent":95,"criticalUptimePercent":80,"slowThresholdMs":1500,"isDefault":false,"usage":{"usedByCount":3,"linkedCheckNames":[]}}
    ]
    """;

    private const string WebhooksJson = """
    [
      {"id":1,"name":"Ops hook","url":"https://hooks.ex.com/err","isEnabled":true,"throttleMinutes":0,"usage":{"usedByCount":4,"linkedCheckNames":[]}},
      {"id":2,"name":"Statuspage","url":"https://sp.ex.com/x","isEnabled":true,"throttleMinutes":5,"usage":{"usedByCount":1,"linkedCheckNames":[]}}
    ]
    """;

    private const string ProfilesJson = """
    [
      {"id":1,"name":"On-call email","emailEnabled":true,"emailRecipients":"oncall@ex.com","usesSiteDefaultRecipients":false,"webPushEnabled":false,"usage":{"usedByCount":2,"linkedCheckNames":[]}}
    ]
    """;

    private sealed record Recorded(string Method, string Path, string? Body);

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

    /// <summary>#432: a context whose responder can return a pending Task, so a test can hold a
    /// response open and reproduce a render-ordering race. The sync <see cref="Ctx"/> above
    /// cannot: its responder returns immediately, which is precisely the timing that HIDES the
    /// bug this file's race test is about.</summary>
    private static (BunitTestContext ctx, List<Recorded> requests) CtxAsync(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var ctx = new BunitTestContext();
        var requests = new List<Recorded>();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new AsyncRoutingHandler(requests, responder)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return (ctx, requests);
    }

    private static HttpResponseMessage DefaultRoutes(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/admin/slas" => Json(SlasJson),
        "/admin/webhooks" => Json(WebhooksJson),
        "/admin/alert-profiles" => Json(ProfilesJson),
        "/statuscheck/edit" => Json("{}"),
        _ => Json("{}"),
    };

    /// <summary>An existing check linked to SLA 2 + webhook 1 (the read-only
    /// round-trip ids the API serves).</summary>
    private static StatusCheckViewModel ExistingCheck() => new()
    {
        Id = 5,
        Title = "Public API",
        StatusCheckUrl = "https://api.example.com/health",
        ExpectedStatusCode = 200,
        IntervalSeconds = 30,
        Enabled = true,
        LinkedSlaId = 2,
        LinkedSlaName = "Messenger 95",
        LinkedWebhookIds = new List<long> { 1 },
        LinkedAlertProfileIds = new List<long>(),
        AlertOnFailureThreshold = 3,
        AlertOnOutageMinutes = 5,
        AlertThrottleMinutes = 15,
        AlertOnRecovery = true,
    };

    private static async Task<IRenderedComponent<MudDialogProvider>> Open(BunitTestContext ctx, StatusCheckViewModelBase vm)
    {
        ctx.RenderComponent<MudPopoverProvider>();   // the SLA MudSelect needs it
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => service.ShowAsync<StatusCheckEditDialog>("check",
            new DialogParameters<StatusCheckEditDialog> { { x => x.StatusCheck, vm } }));
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("EDIT CHECK") || provider.Markup.Contains("ADD CHECK")));
        return provider;
    }

    // ---- structure -----------------------------------------------------------

    [TestMethod]
    public async Task Dialog_RendersTheThreeSections_PickersListEntities_NoLegacyMsField()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var provider = await Open(ctx, ExistingCheck());

        provider.WaitForAssertion(() =>
        {
            // #336: three sections — Provider folded into Monitoring, the
            // Automation checkbox folded into Alerting. The name rides in the
            // .sect-name span (the header also carries the manage hint).
            var sections = provider.FindAll(".hud-dialog-section .sect-name").Select(s => s.TextContent.Trim()).ToList();
            CollectionAssert.AreEqual(new[] { "Basics", "Monitoring", "Alerting" }, sections);
        });

        // #312: the provider Type selector + the generated http config fields
        // (no per-type hardcoded form — url/expectedStatusCode come from the schema).
        Assert.IsTrue(provider.Markup.Contains("chk-provider-type"), "provider Type selector renders");
        Assert.IsTrue(provider.Markup.Contains("chk-cfg-url"), "schema-driven URL field renders");
        Assert.IsTrue(provider.Markup.Contains("chk-cfg-expectedStatusCode"), "schema-driven expected-status field renders");

        // The legacy threshold field is GONE — the SLA defines it now.
        Assert.IsFalse(provider.Markup.Contains("Expected Response Time"), "no per-check ms field");
        Assert.IsFalse(provider.Markup.Contains("Max resp"));
        provider.Find(".chk-sla");
        Assert.IsTrue(provider.Markup.Contains("defines slow threshold + day color tolerance"), "SLA helper");
        Assert.IsTrue(provider.Markup.Contains("manage on Admin → SLAs"));
        // #336: the Alerting header carries one combined manage hint.
        Assert.IsTrue(provider.Markup.Contains("manage on Admin → Webhooks · Alerts"));

        // Pickers list the stubbed entities; linked ones are selected.
        provider.WaitForAssertion(() =>
        {
            var webhookChips = provider.Find(".chk-webhooks").QuerySelectorAll(".pick-chip").ToList();
            Assert.AreEqual(2, webhookChips.Count);
            Assert.IsTrue(webhookChips[0].TextContent.Contains("Ops hook"));
            Assert.IsTrue(webhookChips[0].ClassList.Contains("on"), "linked webhook preselected");
            Assert.IsTrue(webhookChips[1].TextContent.Contains("Statuspage"));
            Assert.IsFalse(webhookChips[1].ClassList.Contains("on"));

            var profileChips = provider.Find(".chk-profiles").QuerySelectorAll(".pick-chip").ToList();
            Assert.AreEqual(1, profileChips.Count);
            Assert.IsTrue(profileChips[0].TextContent.Contains("On-call email"));
            Assert.IsFalse(profileChips[0].ClassList.Contains("on"));
        });

        // The labeled trigger fields (the per-check rules that stay embedded; #336).
        provider.Find(".chk-trigger .chk-fails");
        provider.Find(".chk-trigger .chk-downmin");
        provider.Find(".chk-trigger .chk-throttle");
        Assert.IsTrue(provider.Markup.Contains("Notify on recovery"));
        Assert.IsTrue(provider.Markup.Contains("Auto-draft incident if down persists"));
    }

    // ---- #312: provider seam in the dialog -----------------------------------

    [TestMethod]
    public async Task UnknownProvider_ShowsDisabledBanner_AndIsNotSilentlyRewrittenOnSave()
    {
        var (ctx, requests) = Ctx();
        using var _ctx = ctx;
        var vm = ExistingCheck();
        vm.ProviderType = "tcp";
        vm.ConfigValid = false;
        vm.ConfigError = "unknown provider type 'tcp'";
        var provider = await Open(ctx, vm);

        // The calm "check disabled — fix config" banner is shown.
        provider.WaitForAssertion(() => StringAssert.Contains(provider.Find(".chk-config-disabled").TextContent, "Check disabled"));
        Assert.IsTrue(provider.Markup.Contains("unknown provider type"), "the calm reason is surfaced");

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" })));
        using var doc = JsonDocument.Parse(requests.Single(r => r is { Method: "POST", Path: "/statuscheck/edit" }).Body!);
        Assert.AreEqual("tcp", doc.RootElement.GetProperty("providerType").GetString(),
            "an unknown provider type is never silently rewritten — only an explicit operator pick changes it.");
    }

    // ---- save: the id-array path ---------------------------------------------

    [TestMethod]
    public async Task Save_PostsSlaIdAndIdArrays_NoLegacyFields()
    {
        var (ctx, requests) = Ctx();
        using var _ctx = ctx;
        var provider = await Open(ctx, ExistingCheck());
        provider.WaitForAssertion(() => Assert.AreEqual(2, provider.Find(".chk-webhooks").QuerySelectorAll(".pick-chip").Length));

        // Select the second webhook + the profile via their chips.
        provider.Find(".chk-webhooks").QuerySelectorAll(".pick-chip")[1].Click();
        provider.Render();
        provider.Find(".chk-profiles").QuerySelectorAll(".pick-chip")[0].Click();

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" })));
        var body = requests.Single(r => r is { Method: "POST", Path: "/statuscheck/edit" }).Body!;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.AreEqual(2, root.GetProperty("slaId").GetInt64(), "the picker's SLA rides as slaId");
        CollectionAssert.AreEquivalent(new long[] { 1, 2 },
            root.GetProperty("webhookIds").EnumerateArray().Select(e => e.GetInt64()).ToArray());
        CollectionAssert.AreEquivalent(new long[] { 1 },
            root.GetProperty("alertProfileIds").EnumerateArray().Select(e => e.GetInt64()).ToArray());

        // The legacy embedded fields ride only as inert defaults (the API 422s
        // anything non-empty — this payload must pass its gate).
        Assert.IsFalse(root.GetProperty("isWebHookOnErrorEnabled").GetBoolean());
        Assert.AreEqual("", root.GetProperty("webHookOnErrorUrl").GetString());
        Assert.AreEqual(0, root.GetProperty("throttleWebHookToExecuteOnlyEveryXMinutes").GetInt32());
        Assert.IsFalse(root.GetProperty("emailAlertsEnabled").GetBoolean());
        Assert.AreEqual("", root.GetProperty("emailRecipients").GetString());
        Assert.IsFalse(root.GetProperty("webPushAlertsEnabled").GetBoolean());
        Assert.IsNull(LinkedTargetsAdminApiBridge.Validate(body), "the posted payload passes the API's legacy-field gate");

        // Trigger rules still ride per-check.
        Assert.AreEqual(3, root.GetProperty("alertOnFailureThreshold").GetInt32());
        Assert.AreEqual(5, root.GetProperty("alertOnOutageMinutes").GetInt32());
        Assert.AreEqual(15, root.GetProperty("alertThrottleMinutes").GetInt32());
        Assert.IsTrue(root.GetProperty("alertOnRecovery").GetBoolean());
    }

    [TestMethod]
    public async Task NewCheck_PreselectsTheDefaultSla_AndPostsIt()
    {
        var (ctx, requests) = Ctx();
        using var _ctx = ctx;
        var vm = new StatusCheckViewModel { Title = "fresh", StatusCheckUrl = "https://fresh/health", IntervalSeconds = 30 };
        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => Assert.AreEqual(2, provider.Find(".chk-webhooks").QuerySelectorAll(".pick-chip").Length));

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" })));
        using var doc = JsonDocument.Parse(requests.Single(r => r is { Method: "POST", Path: "/statuscheck/edit" }).Body!);
        Assert.AreEqual(1, doc.RootElement.GetProperty("slaId").GetInt64(), "a new check preselects the ★ default");
        Assert.AreEqual(0, doc.RootElement.GetProperty("webhookIds").GetArrayLength());
        Assert.AreEqual(0, doc.RootElement.GetProperty("alertProfileIds").GetArrayLength());
    }

    // ---- validation names the section -----------------------------------------

    [TestMethod]
    public async Task Validation_MissingBasics_NamesTheSection_NoPost()
    {
        var (ctx, requests) = Ctx();
        using var _ctx = ctx;
        var vm = new StatusCheckViewModel { Title = "", StatusCheckUrl = "", IntervalSeconds = 30 };
        var provider = await Open(ctx, vm);

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() => StringAssert.Contains(provider.Find(".chk-validation").TextContent, "Basics:",
            "the blocking message names the offending section"));
        Assert.IsFalse(requests.Any(r => r.Path == "/statuscheck/edit"), "invalid form never posts");
    }

    [TestMethod]
    public void Validation_EverySectionRule_NamesItsSection()
    {
        // The Monitoring/Notifications branches are defense-in-depth — the
        // clamping numeric fields make them unreachable through the UI, so
        // they're exercised directly (the Basics branch is covered end-to-end
        // above).
        StringAssert.StartsWith(StatusCheckEditDialog.ValidateSections(
            new StatusCheckViewModel { Title = "", StatusCheckUrl = "", IntervalSeconds = 30 })!, "Basics:");
        StringAssert.StartsWith(StatusCheckEditDialog.ValidateSections(
            new StatusCheckViewModel { Title = "x", StatusCheckUrl = "https://x", IntervalSeconds = 2 })!, "Monitoring:");
        StringAssert.StartsWith(StatusCheckEditDialog.ValidateSections(
            new StatusCheckViewModel { Title = "x", StatusCheckUrl = "https://x", IntervalSeconds = 30, AlertOnFailureThreshold = -1 })!, "Alerting:");
        Assert.IsNull(StatusCheckEditDialog.ValidateSections(
            new StatusCheckViewModel { Title = "x", StatusCheckUrl = "https://x", IntervalSeconds = 30 }));
    }

    [TestMethod]
    public void Validation_HttpProvider_RequiresUrl()
    {
        // #320: a URL is required only when the provider declares a "url" config field
        // (the http pull provider) — modelled by the presence of the key.
        // #336: the URL field renders in the Monitoring section now, so the
        // blocking message names that section.
        var http = new StatusCheckViewModel { Title = "x", StatusCheckUrl = "", IntervalSeconds = 30, ProviderType = "http" };
        http.ProviderConfig["url"] = "";
        StringAssert.StartsWith(StatusCheckEditDialog.ValidateSections(http)!, "Monitoring:");

        http.ProviderConfig["url"] = "https://api/health";
        http.StatusCheckUrl = "https://api/health";
        Assert.IsNull(StatusCheckEditDialog.ValidateSections(http), "an http check with a URL validates.");
    }

    [TestMethod]
    public void Validation_HeartbeatProvider_NeedsNoUrl()
    {
        // #320: a push (heartbeat) provider declares no "url" field, so an empty
        // StatusCheckUrl must NOT block the save — the check is driven by inbound pings.
        var hb = new StatusCheckViewModel { Title = "Nightly job", StatusCheckUrl = "", IntervalSeconds = 60, ProviderType = "heartbeat" };
        hb.ProviderConfig["intervalSeconds"] = "3600";
        hb.ProviderConfig["graceSeconds"] = "300";
        Assert.IsNull(StatusCheckEditDialog.ValidateSections(hb),
            "a heartbeat check with no URL is valid — it's a push provider.");
    }

    // Two registered providers so the dialog's Type selector can switch between them.
    private const string ProvidersJson = """
    [
      {"typeId":"http","displayName":"HTTP(S)","icon":"link","schemaVersion":1,"fields":[
        {"key":"url","label":"URL","kind":"text","required":true},
        {"key":"expectedStatusCode","label":"Expected status","kind":"number","required":true}
      ]},
      {"typeId":"heartbeat","displayName":"Agent heartbeat","icon":"pulse","schemaVersion":1,"fields":[
        {"key":"intervalSeconds","label":"Expected interval (s)","kind":"number","required":true},
        {"key":"graceSeconds","label":"Grace (s)","kind":"number","required":true}
      ]},
      {"typeId":"ai","displayName":"AI / LLM canary","icon":"robot","schemaVersion":1,"fields":[
        {"key":"baseUrl","label":"Base URL","kind":"text","required":true},
        {"key":"model","label":"Model","kind":"text","required":true},
        {"key":"apiKey","label":"API key","kind":"secret","required":false},
        {"key":"prompt","label":"Canary prompt","kind":"text","required":true},
        {"key":"expectContains","label":"Response must contain","kind":"text","required":true}
      ]}
    ]
    """;

    private static HttpResponseMessage WithProviders(HttpRequestMessage request)
        => request.RequestUri!.AbsolutePath == "/statuscheck/providers" ? Json(ProvidersJson) : DefaultRoutes(request);

    [TestMethod]
    public async Task RequiredSchemaFields_BlankOnSave_BlockClientSide_NoServerRoundTrip()
    {
        // The reported bug: create an AI/LLM check, leave a required schema field (baseUrl,
        // model, prompt, expected-content) blank, hit Save — and it "bitches AFTER I saved
        // and doesn't save the data." Root cause: the schema-driven config fields carried
        // NO client validation, so _form.Validate() passed and the check was POSTed, only to
        // 422 on the server (AiCheckConfig.TryParse requires baseUrl + model). None of the
        // AI fields are covered by ValidateSections either. This drives the real dialog:
        // switch to the AI provider, leave the required fields blank, click Save, and assert
        // the save is blocked CLIENT-SIDE (no POST) with an inline error — not a post-save 422.
        var (ctx, requests) = Ctx(WithProviders);
        using var _ctx = ctx;
        var vm = new StatusCheckViewModel { Title = "AI canary", StatusCheckUrl = "", IntervalSeconds = 60 };
        var provider = await Open(ctx, vm);

        var typeSelect = provider.FindComponents<MudSelect<string>>()
            .First(s => (s.Instance.Class ?? "").Contains("chk-provider-type"));
        await provider.InvokeAsync(() => typeSelect.Instance.ValueChanged.InvokeAsync("ai"));
        provider.Render();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("chk-cfg-baseUrl"), "AI fields render"));

        // #433: a NEW AI check now stages — model/prompt/expected-content stay collapsed until
        // the endpoint has answered. Take the escape hatch to reveal them all without
        // validating, which is exactly the path an operator uses when the endpoint isn't
        // reachable from the server. The #384 guarantee below must hold on that path too:
        // staging must not become a way to smuggle a blank required field past validation.
        provider.Find(".chk-ai-manual").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("chk-cfg-model"), "the escape hatch reveals every field"));

        // Leave every required AI field blank, then Save.
        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        // 1. No POST — the invalid check must NOT reach the server.
        provider.WaitForAssertion(() => Assert.IsFalse(
            requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" }),
            "an AI check with blank required fields must be blocked client-side, not POSTed and 422'd."));

        // 2. The dialog stays open (didn't close on a successful save).
        Assert.IsTrue(provider.Markup.Contains("ADD CHECK"), "the dialog must stay open when validation fails.");

        // 3. An inline required-field error is shown (the whole point — validate BEFORE save).
        provider.WaitForAssertion(() => Assert.IsTrue(
            provider.Markup.Contains("Base URL is required.")
            || provider.Markup.Contains("Model is required.")
            || provider.Markup.Contains("is required."),
            "the required schema fields must surface an inline validation error."));

        // 4. Fill the required fields → the save now goes through.
        void SetField(string key, string val)
        {
            var tf = provider.FindComponents<MudTextField<string>>()
                .First(f => (f.Instance.Class ?? "").Contains($"chk-cfg-{key}"));
            provider.InvokeAsync(() => tf.Instance.ValueChanged.InvokeAsync(val)).GetAwaiter().GetResult();
        }
        SetField("baseUrl", "https://api.openai.com/v1");
        SetField("model", "gpt-4o-mini");
        SetField("prompt", "ping");
        SetField("expectContains", "pong");
        provider.Render();

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(
            requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" }),
            "once the required fields are filled, the save must go through."));
    }

    [TestMethod]
    public async Task SwitchHttpToHeartbeat_DropsStaleUrlKey_SavesWithoutUrl()
    {
        // Regression: the dialog opens on the default http provider, which seeds a "url"
        // config key. Switching to heartbeat (a URL-less push provider) used to LEAVE that
        // stale key in the map, so ValidateSections still demanded a URL and the save was
        // blocked — even though heartbeat has no URL field. This drives the real switch.
        var (ctx, requests) = Ctx(WithProviders);
        using var _ctx = ctx;
        var vm = new StatusCheckViewModel { Title = "Nightly job", StatusCheckUrl = "", IntervalSeconds = 60 };
        var provider = await Open(ctx, vm);

        // Sanity: the dialog started on http and seeded the url field.
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("chk-cfg-url"), "opens on http with a url field"));

        // Switch the provider Type to heartbeat via the selector's ValueChanged (→ OnProviderTypeChanged).
        var typeSelect = provider.FindComponents<MudSelect<string>>()
            .First(s => (s.Instance.Class ?? "").Contains("chk-provider-type"));
        await provider.InvokeAsync(() => typeSelect.Instance.ValueChanged.InvokeAsync("heartbeat"));
        provider.Render();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("chk-cfg-intervalSeconds"), "heartbeat fields render"));
        Assert.IsFalse(provider.Markup.Contains("chk-cfg-url"), "the http url field is gone after the switch");

        // Fill heartbeat's own required schema fields (interval/grace) — they start blank
        // and are Required in the schema, so client validation now (correctly) demands them.
        foreach (var (key, val) in new[] { ("intervalSeconds", "3600"), ("graceSeconds", "300") })
        {
            var tf = provider.FindComponents<MudTextField<string>>()
                .First(f => (f.Instance.Class ?? "").Contains($"chk-cfg-{key}"));
            await provider.InvokeAsync(() => tf.Instance.ValueChanged.InvokeAsync(val));
        }
        provider.Render();

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        // The save must go through — no spurious "a check URL is required" block.
        provider.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" }),
            "a heartbeat check saves without a URL"));
        using var doc = JsonDocument.Parse(requests.Single(r => r is { Method: "POST", Path: "/statuscheck/edit" }).Body!);
        Assert.AreEqual("heartbeat", doc.RootElement.GetProperty("providerType").GetString());
    }

    // ---- quick-create ("+ New…") ------------------------------------------------

    [TestMethod]
    public async Task QuickCreateWebhook_OpensEditDialogInline_RefreshesPicker_AutoSelectsTheNewTarget()
    {
        bool created = false;
        const string createdJson = """{"id":9,"name":"New hook","url":"https://new.ex.com/x","isEnabled":true,"throttleMinutes":0,"usage":{"usedByCount":0,"linkedCheckNames":[]}}""";
        var (ctx, requests) = Ctx(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/admin/webhooks" && request.Method == HttpMethod.Post)
            {
                created = true;
                return Json(createdJson);
            }
            if (request.RequestUri!.AbsolutePath == "/admin/webhooks" && request.Method == HttpMethod.Get && created)
            {
                return Json(WebhooksJson.TrimEnd().TrimEnd(']').TrimEnd() + ",\n" + createdJson + "\n]");
            }
            return DefaultRoutes(request);
        });
        using var _ctx = ctx;
        var provider = await Open(ctx, ExistingCheck());
        provider.WaitForAssertion(() => Assert.AreEqual(2, provider.Find(".chk-webhooks").QuerySelectorAll(".pick-chip").Length));

        provider.Find(".chk-webhook-new").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("ADD WEBHOOK"), "the webhook edit dialog opens inline"));

        // Fill the inner dialog (name + URL) and save it.
        var inner = provider.FindAll(".mud-dialog").Last();
        var inputs = inner.QuerySelectorAll("input").ToList();
        inputs[0].Change("New hook");
        inputs[1].Change("https://new.ex.com/x");
        provider.FindAll(".mud-dialog-actions button").Last(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/admin/webhooks" }), "quick-create posts the new target");
            var chips = provider.Find(".chk-webhooks").QuerySelectorAll(".pick-chip").ToList();
            Assert.AreEqual(3, chips.Count, "the picker refreshed with the new entity");
            var newChip = chips.Single(c => c.TextContent.Contains("New hook"));
            Assert.IsTrue(newChip.ClassList.Contains("on"), "the new entity is auto-selected");
        });

        // And the subsequent save carries the new id.
        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" })));
        using var doc = JsonDocument.Parse(requests.Single(r => r is { Method: "POST", Path: "/statuscheck/edit" }).Body!);
        CollectionAssert.Contains(doc.RootElement.GetProperty("webhookIds").EnumerateArray().Select(e => e.GetInt64()).ToList(), 9L);
    }

    [TestMethod]
    public async Task QuickCreateSla_AutoSelectsTheNewSla()
    {
        bool created = false;
        const string createdJson = """{"id":7,"name":"Gold","targetUptimePercent":99.9,"criticalUptimePercent":99,"slowThresholdMs":250,"isDefault":false,"usage":{"usedByCount":0,"linkedCheckNames":[]}}""";
        var (ctx, requests) = Ctx(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/admin/slas" && request.Method == HttpMethod.Post)
            {
                created = true;
                return Json(createdJson);
            }
            if (request.RequestUri!.AbsolutePath == "/admin/slas" && request.Method == HttpMethod.Get && created)
            {
                return Json(SlasJson.TrimEnd().TrimEnd(']').TrimEnd() + ",\n" + createdJson + "\n]");
            }
            return DefaultRoutes(request);
        });
        using var _ctx = ctx;
        var provider = await Open(ctx, ExistingCheck());
        provider.WaitForAssertion(() => Assert.AreEqual(2, provider.Find(".chk-webhooks").QuerySelectorAll(".pick-chip").Length));

        provider.Find(".chk-sla-new").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("ADD SLA"), "the SLA edit dialog opens inline"));

        // Name it (targets/threshold keep the valid defaults) and save.
        var inner = provider.FindAll(".mud-dialog").Last();
        inner.QuerySelectorAll("input").First().Change("Gold");
        provider.FindAll(".mud-dialog-actions button").Last(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/admin/slas" })));

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" })));
        using var doc = JsonDocument.Parse(requests.Single(r => r is { Method: "POST", Path: "/statuscheck/edit" }).Body!);
        Assert.AreEqual(7, doc.RootElement.GetProperty("slaId").GetInt64(), "the quick-created SLA is auto-selected and posted");
    }

    // ---- #432: the Type selector must never render an empty box --------------------

    /// <summary>What MudSelect PAINTS is the matching MudSelectItem's content, not its
    /// <c>Text</c> — the value it derives from the converter lives in a hidden input the
    /// operator never sees. So the assertion has to read the visible slot; reading the input
    /// would pass for the wrong reason.</summary>
    private static string PaintedProviderType(IRenderedComponent<MudDialogProvider> provider)
        => provider.Find(".chk-provider-type div.mud-input-slot").TextContent.Trim();

    /// <summary>
    /// THE REPORTED BUG, reproduced. An operator opens the dialog and the Type box shows
    /// something that is not a provider — the raw id, or nothing — until they touch the
    /// dropdown, at which point it repairs itself.
    ///
    /// <para>The cause is a race, and reproducing it needs the provider fetch to be genuinely
    /// asynchronous. That is why the demo gallery does NOT show this: <c>DemoApiHandler</c>
    /// answers <c>/statuscheck/providers</c> in-process, so the await completes without ever
    /// yielding and the very first render already has the descriptors. Against the real API it
    /// is a network round-trip, the first render has an EMPTY provider list, and MudSelect is
    /// constructed with no <c>MudSelectItem</c> to paint from. Registering items later does not
    /// re-run that; only picking an option does.</para>
    ///
    /// <para>So this test holds the providers response open until after the dialog has rendered
    /// once — exactly the real-world ordering — and only then releases it.</para>
    /// </summary>
    [TestMethod]
    public async Task ProviderTypeSelector_WhenDescriptorsArriveAfterTheFirstRender_StillPaintsTheDisplayName()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, _) = CtxAsync(request => request.RequestUri!.AbsolutePath == "/statuscheck/providers"
            ? release.Task                                    // held open past the first render
            : Task.FromResult(DefaultRoutes(request)));
        using var _ctx = ctx;

        // The dialog renders with an EMPTY provider list — the real-app ordering — and only
        // then do the descriptors land.
        var provider = await Open(ctx, ExistingCheck());
        release.SetResult(Json(ProvidersJson));

        provider.WaitForAssertion(() =>
        {
            var painted = PaintedProviderType(provider);
            Assert.AreNotEqual(string.Empty, painted,
                "the Type box must never be painted empty once the descriptors have arrived");
            Assert.AreNotEqual("http", painted,
                "…and it must never show the raw TypeId, which matches nothing in its own dropdown");
            Assert.AreEqual("HTTP(S)", painted);
        });
    }

    /// <summary>The registered case, unchanged by #432 and pinned so it stays that way: the
    /// descriptor's DisplayName is painted, with no interaction.</summary>
    [TestMethod]
    public async Task ProviderTypeSelector_RegisteredType_PaintsTheDisplayName()
    {
        var (ctx, _) = Ctx(WithProviders);
        using var _ctx = ctx;
        var provider = await Open(ctx, ExistingCheck());

        provider.WaitForAssertion(() =>
            Assert.AreEqual("HTTP(S)", PaintedProviderType(provider),
                "a registered type paints its DisplayName"));
    }

    /// <summary>THE REGRESSION. A stored type no registered provider declares had no matching
    /// item, and MudSelect paints nothing without one — so the operator got a labelled "TYPE"
    /// box that was completely empty, on the very check the disabled-config banner is telling
    /// them to fix. It now names the type and says it isn't installed.</summary>
    [TestMethod]
    public async Task ProviderTypeSelector_UnregisteredType_NamesTheTypeInsteadOfRenderingBlank()
    {
        var (ctx, _) = Ctx(WithProviders);
        using var _ctx = ctx;
        var vm = ExistingCheck();
        vm.ProviderType = "tcp";
        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() =>
        {
            var painted = PaintedProviderType(provider);
            Assert.AreNotEqual(string.Empty, painted, "the Type box is never painted empty");
            StringAssert.Contains(painted, "tcp", "the unregistered type names itself");
            StringAssert.Contains(painted, "not installed", "…and says why it isn't a normal option");
        });
    }

    /// <summary>The invariant that makes the above safe: naming an unregistered type is a
    /// DISPLAY affordance only. Saving still round-trips the stored id untouched — #312's
    /// "unknown disables calmly, never silently defaults" rule is not weakened by giving the
    /// operator something to look at.</summary>
    [TestMethod]
    public async Task ProviderTypeSelector_UnregisteredType_IsStillNeverRewrittenOnSave()
    {
        var (ctx, requests) = Ctx(WithProviders);
        using var _ctx = ctx;
        var vm = ExistingCheck();
        vm.ProviderType = "tcp";
        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => Assert.AreNotEqual(string.Empty, PaintedProviderType(provider)));

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        provider.WaitForAssertion(() => Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/statuscheck/edit" })));
        using var doc = JsonDocument.Parse(requests.Single(r => r is { Method: "POST", Path: "/statuscheck/edit" }).Body!);
        Assert.AreEqual("tcp", doc.RootElement.GetProperty("providerType").GetString());
    }

    /// <summary>Picking a real option paints that option and rewrites the stored type — the
    /// selector's display and its value must not drift apart.</summary>
    [TestMethod]
    public async Task ProviderTypeSelector_AfterPicking_PaintsThePickedDisplayName()
    {
        var (ctx, _) = Ctx(WithProviders);
        using var _ctx = ctx;
        var vm = ExistingCheck();
        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => Assert.AreEqual("HTTP(S)", PaintedProviderType(provider)));

        // Drive the ValueChanged path the way MudSelect does when an item is clicked.
        var select = provider.FindComponents<MudSelect<string>>()
            .Single(s => s.Instance.Class is not null && s.Instance.Class.Contains("chk-provider-type"));
        await provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("ai"));

        provider.WaitForAssertion(() =>
        {
            Assert.AreEqual("AI / LLM canary", PaintedProviderType(provider));
            Assert.AreEqual("ai", vm.ProviderType);
        });
    }

    // ---- fixtures -----------------------------------------------------------

    /// <summary>Round-trips the posted JSON through the VM and the API's
    /// legacy-field gate, proving the dialog's payload passes it.</summary>
    private static class LinkedTargetsAdminApiBridge
    {
        public static string? Validate(string json)
        {
            var vm = JsonSerializer.Deserialize<StatusCheckViewModelBase>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return SuperStatus.ApiService.LinkedTargetsAdminApi.ValidateEditPayload(vm);
        }
    }

    private sealed class AsyncRoutingHandler(List<Recorded> requests, Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add(new Recorded(request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return await responder(request);
        }
    }

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
