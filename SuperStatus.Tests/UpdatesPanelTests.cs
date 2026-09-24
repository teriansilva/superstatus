using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #249 (epic #248): the /admin Updates panel renders the right state —
/// up-to-date / update-available / check-failed / edge — from /api/updates.
/// Issue #311 added the in-app "Update now" button; issue #334 added the persisted
/// auto-update toggle + daily UTC schedule, and made the guided shell command a
/// fallback rather than the primary path.
/// </summary>
[TestClass]
public class UpdatesPanelTests
{
    private static BunitTestContext Ctx(string updatesJson, HttpStatusCode code = HttpStatusCode.OK)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new Stub(updatesJson, code)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new UpdatesApiClient(http));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [TestMethod]
    public void UpToDate_showsGreenTag_andNoUpgradeCommand()
    {
        const string json = """
        {"currentVersion":"1.2.0","channel":"latest","status":"uptodate",
         "checkEnabled":true,"lastCheckedUtc":"2026-06-01T00:00:00Z",
         "latestVersion":"1.2.0","latestNotesUrl":null,"lastCheckError":null,
         "autoUpdateEnabled":false,"autoUpdateTimeUtc":"03:00:00",
         "upgradeCommand":"docker compose pull && docker compose up -d"}
        """;
        using var ctx = Ctx(json);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Up to date"));
            Assert.IsTrue(cut.Markup.Contains("v1.2.0"));
            Assert.IsFalse(cut.Markup.Contains("docker compose pull"), "no upgrade command when up to date");
        });
    }

    [TestMethod]
    public void CheckFailed_showsCalmCouldntCheck_withError_andLastKnown()
    {
        const string json = """
        {"currentVersion":"1.2.0","channel":"latest","status":"uptodate",
         "checkEnabled":true,"lastCheckedUtc":"2026-06-01T00:00:00Z",
         "latestVersion":"1.2.0","latestNotesUrl":null,
         "lastCheckError":"GitHub releases API returned 403 Forbidden",
         "autoUpdateEnabled":false,"autoUpdateTimeUtc":"03:00:00",
         "upgradeCommand":"docker compose pull && docker compose up -d"}
        """;
        using var ctx = Ctx(json);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Couldn't check"));
            Assert.IsTrue(cut.Markup.Contains("403 Forbidden"));
            Assert.IsTrue(cut.Markup.Contains("last known"));
        });
    }

    [TestMethod]
    public void EdgeBuild_showsDevelopmentBuild_noLatestRow()
    {
        const string json = """
        {"currentVersion":"edge","channel":"edge","status":"edge",
         "checkEnabled":true,"lastCheckedUtc":null,"latestVersion":null,
         "latestNotesUrl":null,"lastCheckError":null,
         "autoUpdateEnabled":false,"autoUpdateTimeUtc":"03:00:00",
         "upgradeCommand":"docker compose pull && docker compose up -d"}
        """;
        using var ctx = Ctx(json);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Development build"));
            Assert.IsFalse(cut.Markup.Contains(">Latest<"), "no Latest row for an edge build");
        });
    }

    [TestMethod]
    public void ApiUnavailable_showsRetryableUnavailableState_notPermanentLoading()
    {
        // GET /api/updates fails → UpdatesApiClient.GetAsync returns null. The panel
        // must distinguish this loaded-but-unavailable state from initial loading.
        using var ctx = Ctx("", HttpStatusCode.ServiceUnavailable);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Service unavailable"));
            Assert.IsTrue(cut.Markup.Contains("Retry"));
            Assert.IsFalse(cut.Markup.Contains("Checking for updates"), "not stuck in the loading state");
        });
    }

    // ── Issue #311/#334: in-app "Update now" + the auto-update control ─────────

    /// <summary>Default install: engine present ⇒ canApplyInApp, auto-update off at 03:00.</summary>
    private const string AvailableCanApplyJson = """
        {"currentVersion":"1.3.2","channel":"latest","status":"available",
         "checkEnabled":true,"lastCheckedUtc":"2026-06-16T00:00:00Z",
         "latestVersion":"1.3.3","latestNotesUrl":"https://github.com/teriansilva/superstatus/releases/tag/v1.3.3",
         "lastCheckError":null,"autoUpdateEnabled":false,"autoUpdateTimeUtc":"03:00:00",
         "canApplyInApp":true,
         "upgradeCommand":"docker compose pull && docker compose up -d"}
        """;

    /// <summary>Opted-out install (--no-updater): no engine ⇒ no button, no toggle.</summary>
    private const string AvailableNoEngineJson = """
        {"currentVersion":"1.3.2","channel":"latest","status":"available",
         "checkEnabled":true,"lastCheckedUtc":"2026-06-16T00:00:00Z",
         "latestVersion":"1.3.3","latestNotesUrl":null,"lastCheckError":null,
         "autoUpdateEnabled":false,"autoUpdateTimeUtc":"03:00:00","canApplyInApp":false,
         "upgradeCommand":"docker compose pull && docker compose up -d"}
        """;

    [TestMethod]
    public void UpdateAvailable_canApply_showsUpdateNowButton_andHidesGuidedCommand()
    {
        using var ctx = Ctx(AvailableCanApplyJson);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Update now"), "one-click button shown when canApplyInApp");
            Assert.IsFalse(cut.Markup.Contains("docker compose pull"), "guided command hidden when the button is available");
            Assert.IsTrue(cut.Markup.Contains("release notes"), "notes link still present");
        });
    }

    [TestMethod]
    public void EnginePresent_rendersAutoUpdateToggleAndUtcSchedule()
    {
        // #334: the whole point — the operator sets this from the web, never the server.
        using var ctx = Ctx(AvailableCanApplyJson);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Automatic updates"));
            Assert.AreEqual(2, cut.FindAll("button.up-seg-btn").Count, "an on/off segmented control");
            var time = cut.Find("input.up-time-input");
            Assert.AreEqual("03:00", time.GetAttribute("value"));
            Assert.IsTrue(cut.Markup.Contains("UTC"), "the schedule is labelled UTC (v1 is UTC-only)");
        });
    }

    [TestMethod]
    public void AutoUpdateOff_disablesTheScheduleInput_butKeepsUpdateNowUsable()
    {
        // Mockup state D: schedule dims when off, "Update now" still works.
        using var ctx = Ctx(AvailableCanApplyJson);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Find("input.up-time-input").HasAttribute("disabled"), "schedule is inert while auto-update is off");
            Assert.IsFalse(cut.Find("button.up-apply").HasAttribute("disabled"), "Update now stays available");
        });
    }

    [TestMethod]
    public void NoEngine_hidesToggleAndButton_andFallsBackToGuidedCommand()
    {
        // Opt-out (SUPERSTATUS_UPDATE_ENGINE=none): nothing in-app can apply an update.
        using var ctx = Ctx(AvailableNoEngineJson);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsFalse(cut.Markup.Contains("Update now"), "no one-click button without an engine");
            Assert.AreEqual(0, cut.FindAll("button.up-seg-btn").Count, "no auto-update toggle without an engine");
            Assert.IsTrue(cut.Markup.Contains("docker compose pull"), "guided command shown instead");
            Assert.IsTrue(cut.Markup.Contains("no update engine"), "says why");
        });
    }

    [TestMethod]
    public void TurningAutoUpdateOn_postsThePolicy_andReflectsThePersistedState()
    {
        var stub = new RoutedStub(AvailableCanApplyJson, """{"accepted":true}""", HttpStatusCode.Accepted);
        using var ctx = CtxWith(stub);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll("button.up-seg-btn").Count));

        cut.FindAll("button.up-seg-btn")[0].Click();   // "On"

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("/api/updates/auto", stub.AutoPath, "the toggle persists via POST /api/updates/auto");
            StringAssert.Contains(stub.AutoBody, "\"enabled\":true");
            StringAssert.Contains(stub.AutoBody, "\"time\":\"03:00\"", "time is sent as strict HH:mm");
            Assert.IsFalse(cut.Find("input.up-time-input").HasAttribute("disabled"), "schedule becomes editable once on");
            Assert.AreEqual("true", cut.FindAll("button.up-seg-btn")[0].GetAttribute("aria-pressed"));
        });
    }

    // A real browser reports <input type="time"> to Blazor as "HH:mm:ss", not "HH:mm".
    // Parsing only "HH:mm" made the panel silently drop every schedule change — caught by
    // web/visual/updates-panel.spec.mjs, not by bUnit, because .Change() injects a raw
    // string. Both shapes are pinned here so the regression can't come back.
    [DataTestMethod]
    [DataRow("02:30:00", DisplayName = "browser payload (HH:mm:ss)")]
    [DataRow("02:30", DisplayName = "wire payload (HH:mm)")]
    public void ChangingTheSchedule_postsTheNewTimeAsHHmm(string browserValue)
    {
        var stub = new RoutedStub(AvailableCanApplyJson, """{"accepted":true}""", HttpStatusCode.Accepted);
        using var ctx = CtxWith(stub);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll("button.up-seg-btn").Count));

        cut.FindAll("button.up-seg-btn")[0].Click();                  // on, so the input is enabled
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("input.up-time-input").HasAttribute("disabled")));
        cut.Find("input.up-time-input").Change(browserValue);

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, stub.AutoCalls, "the schedule change is its own write");
            StringAssert.Contains(stub.AutoBody, "\"time\":\"02:30\"", "always normalized to HH:mm on the wire");
            StringAssert.Contains(stub.AutoBody, "\"enabled\":true", "changing the time keeps it enabled");
            Assert.AreEqual("02:30", cut.Find("input.up-time-input").GetAttribute("value"));
        });
    }

    [TestMethod]
    public void ChangingTheSchedule_toGarbage_doesNotPersistAnything()
    {
        // Strict: a value that isn't a 24-hour time is rejected, never coerced to midnight.
        var stub = new RoutedStub(AvailableCanApplyJson, """{"accepted":true}""", HttpStatusCode.Accepted);
        using var ctx = CtxWith(stub);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll("button.up-seg-btn").Count));

        cut.FindAll("button.up-seg-btn")[0].Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, stub.AutoCalls));
        cut.Find("input.up-time-input").Change("");                   // cleared field

        cut.WaitForAssertion(() => Assert.AreEqual(1, stub.AutoCalls, "no write for an unparseable time"));
    }

    [TestMethod]
    public void AutoUpdateSaveFailure_revertsTheToggle_ratherThanLyingAboutIt()
    {
        // A control that silently didn't save is worse than one that says so.
        var stub = new RoutedStub(AvailableCanApplyJson, """{"accepted":true}""", HttpStatusCode.Accepted)
        {
            AutoCode = HttpStatusCode.InternalServerError,
        };
        using var ctx = CtxWith(stub);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() => Assert.AreEqual(2, cut.FindAll("button.up-seg-btn").Count));

        cut.FindAll("button.up-seg-btn")[0].Click();   // "On" → server rejects

        cut.WaitForAssertion(() =>
        {
            var off = cut.FindAll("button.up-seg-btn")[1];
            Assert.AreEqual("true", off.GetAttribute("aria-pressed"), "toggle reverted to Off after a failed save");
            Assert.IsTrue(cut.Find("input.up-time-input").HasAttribute("disabled"), "schedule re-disabled");
        });
    }

    [TestMethod]
    public void ClickUpdateNow_accepted_entersUpdatingState()
    {
        using var ctx = Ctx2(AvailableCanApplyJson, """{"accepted":true,"error":null}""", HttpStatusCode.Accepted);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Update now")));
        cut.Find("button.up-apply").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Updating…"), "accepted trigger → Updating… state");
            Assert.IsTrue(cut.Markup.Contains("reconnect"), "honest restart/reconnect note");
        });
    }

    [TestMethod]
    public void ClickUpdateNow_failed_showsErrorAndGuidedFallback()
    {
        const string applyErr = """{"accepted":false,"error":"Couldn't reach the updater. Make sure Watchtower is running."}""";
        using var ctx = Ctx2(AvailableCanApplyJson, applyErr, HttpStatusCode.BadGateway);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Update now")));
        cut.Find("button.up-apply").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Couldn't reach the updater"), "apply failure surfaced");
            Assert.IsTrue(cut.Markup.Contains("docker compose pull"), "guided command offered as fallback on failure");
            Assert.IsFalse(cut.Markup.Contains("Updating…"), "not stuck in updating after a failure");
        });
    }

    // Context whose handler routes /api/updates(/check) to the status JSON,
    // /api/updates/apply to a configurable apply response, and /api/updates/auto to a
    // configurable policy response (recording the request body).
    private static BunitTestContext Ctx2(string statusJson, string applyJson, HttpStatusCode applyCode)
        => CtxWith(new RoutedStub(statusJson, applyJson, applyCode));

    private static BunitTestContext CtxWith(HttpMessageHandler handler)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new UpdatesApiClient(http));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private sealed class Stub(string json, HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(code);
            if (!string.IsNullOrEmpty(json)) resp.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }

    private sealed class RoutedStub(string statusJson, string applyJson, HttpStatusCode applyCode) : HttpMessageHandler
    {
        /// <summary>Status code for POST /api/updates/auto. On success the stub echoes the
        /// posted policy back inside the status payload, exactly as the real endpoint does
        /// (it returns the freshly persisted state) — so the panel adopts what it sent.</summary>
        public HttpStatusCode AutoCode { get; set; } = HttpStatusCode.OK;

        /// <summary>What the panel actually sent to /api/updates/auto (last request).</summary>
        public string AutoBody { get; private set; } = "";
        public string? AutoPath { get; private set; }
        public int AutoCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            if (path.EndsWith("/auto", StringComparison.Ordinal))
            {
                AutoCalls++;
                AutoPath = path;
                AutoBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

                var autoResp = new HttpResponseMessage(AutoCode);
                if (AutoCode == HttpStatusCode.OK)
                    autoResp.Content = new StringContent(EchoPolicy(AutoBody), Encoding.UTF8, "application/json");
                return autoResp;
            }

            var (json, code) = path.EndsWith("/apply", StringComparison.Ordinal)
                ? (applyJson, applyCode)
                : (statusJson, HttpStatusCode.OK);
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>Rebuild the status payload with the posted {enabled, time} folded in.</summary>
        private string EchoPolicy(string requestBody)
        {
            using var posted = JsonDocument.Parse(requestBody);
            var enabled = posted.RootElement.GetProperty("enabled").GetBoolean();
            var time = posted.RootElement.GetProperty("time").GetString();

            using var status = JsonDocument.Parse(statusJson);
            var fields = status.RootElement.EnumerateObject()
                .Where(p => p.Name is not ("autoUpdateEnabled" or "autoUpdateTimeUtc"))
                .Select(p => $"\"{p.Name}\":{p.Value.GetRawText()}");

            return "{" + string.Join(",", fields)
                + $",\"autoUpdateEnabled\":{(enabled ? "true" : "false")},\"autoUpdateTimeUtc\":\"{time}:00\"}}";
        }
    }
}
