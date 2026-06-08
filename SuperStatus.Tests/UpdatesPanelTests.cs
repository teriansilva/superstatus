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

    private sealed class Stub(string json, HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(code);
            if (!string.IsNullOrEmpty(json)) resp.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }
}
