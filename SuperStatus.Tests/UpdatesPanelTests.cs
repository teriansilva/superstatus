using System.Net;
using System.Text;
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
         "autoUpdateActive":false,"upgradeCommand":"docker compose pull && docker compose up -d"}
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
    public void UpdateAvailable_showsCommand_notesLink_andAutoUpdateOn()
    {
        const string json = """
        {"currentVersion":"1.0.0","channel":"latest","status":"available",
         "checkEnabled":true,"lastCheckedUtc":"2026-06-01T00:00:00Z",
         "latestVersion":"1.3.0","latestNotesUrl":"https://github.com/teriansilva/superstatus/releases/tag/v1.3.0",
         "lastCheckError":null,"autoUpdateActive":true,
         "upgradeCommand":"docker compose pull && docker compose up -d"}
        """;
        using var ctx = Ctx(json);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Update available"));
            Assert.IsTrue(cut.Markup.Contains("v1.3.0"));
            Assert.IsTrue(cut.Markup.Contains("docker compose pull"), "shows the guided upgrade command");
            Assert.IsTrue(cut.Markup.Contains("release notes"));
            Assert.IsTrue(cut.Markup.Contains("Watchtower"), "auto-update on → Watchtower note");
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
         "autoUpdateActive":false,"upgradeCommand":"docker compose pull && docker compose up -d"}
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
         "latestNotesUrl":null,"lastCheckError":null,"autoUpdateActive":false,
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

    // ── Issue #311: in-app "Update now" button ────────────────────────────────

    private const string AvailableCanApplyJson = """
        {"currentVersion":"1.3.2","channel":"latest","status":"available",
         "checkEnabled":true,"lastCheckedUtc":"2026-06-16T00:00:00Z",
         "latestVersion":"1.3.3","latestNotesUrl":"https://github.com/teriansilva/superstatus/releases/tag/v1.3.3",
         "lastCheckError":null,"autoUpdateActive":true,"canApplyInApp":true,
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
    public void UpdateAvailable_withoutCanApply_showsGuidedCommand_andNoUpdateNowButton()
    {
        // The existing update-available JSON omits canApplyInApp ⇒ false.
        const string json = """
        {"currentVersion":"1.3.2","channel":"latest","status":"available",
         "checkEnabled":true,"lastCheckedUtc":"2026-06-16T00:00:00Z",
         "latestVersion":"1.3.3","latestNotesUrl":null,"lastCheckError":null,
         "autoUpdateActive":false,"canApplyInApp":false,
         "upgradeCommand":"docker compose pull && docker compose up -d"}
        """;
        using var ctx = Ctx(json);
        var cut = ctx.RenderComponent<UpdatesPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("docker compose pull"), "guided command shown when one-click is unavailable");
            Assert.IsFalse(cut.Markup.Contains("Update now"), "no one-click button without canApplyInApp");
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

    // Context whose handler routes /api/updates(/check) to the status JSON and
    // /api/updates/apply to a configurable apply response.
    private static BunitTestContext Ctx2(string statusJson, string applyJson, HttpStatusCode applyCode)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new RoutedStub(statusJson, applyJson, applyCode)) { BaseAddress = new Uri("http://api.test") };
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var (json, code) = path.EndsWith("/apply", StringComparison.Ordinal)
                ? (applyJson, applyCode)
                : (statusJson, HttpStatusCode.OK);
            var resp = new HttpResponseMessage(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
