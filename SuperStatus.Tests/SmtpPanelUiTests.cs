using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #241 Phase B2 — the SMTP section on the SiteSettingsPanel: renders, masks
/// a stored password, and saves to the dedicated /settings/smtp endpoint.
/// </summary>
[TestClass]
public class SmtpPanelUiTests
{
    private static BunitTestContext Ctx(SiteSettingsViewModel vm, List<string> postPaths)
    {
        var ctx = new BunitTestContext();
        var client = new SettingsApiClient(new HttpClient(new Handler(vm, postPaths)) { BaseAddress = new Uri("http://api.test") });
        ctx.Services.AddSingleton(client);
        // #241 Phase C: SiteSettingsPanel hosts EnablePushButton, which injects this.
        ctx.Services.AddSingleton(new PushApiClient(new HttpClient(new Handler(vm, postPaths)) { BaseAddress = new Uri("http://api.test") }));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [TestMethod]
    public void RendersSmtpSection_MaskedPasswordWhenStored()
    {
        var vm = new SiteSettingsViewModel { SmtpHost = "mail.test", SmtpFromAddress = "alerts@test", SmtpPasswordSet = true };
        using var ctx = Ctx(vm, new());
        var cut = ctx.RenderComponent<EmailAlertsPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Email alerts (SMTP)"));
            Assert.IsTrue(cut.Markup.Contains("mail.test"), "host shown");
            Assert.IsTrue(cut.Markup.Contains("(stored)"), "stored password is masked, not echoed");
            Assert.IsTrue(cut.Markup.Contains("Save email settings"));
            Assert.IsTrue(cut.Markup.Contains("Send test email"));
        });
    }

    [TestMethod]
    public void SaveEmailSettings_PostsToDedicatedSmtpEndpoint()
    {
        var posts = new List<string>();
        using var ctx = Ctx(new SiteSettingsViewModel(), posts);
        var cut = ctx.RenderComponent<EmailAlertsPanel>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Save email settings")));

        var saveSmtp = cut.FindAll("button").First(b => b.TextContent.Contains("Save email settings"));
        saveSmtp.Click();

        cut.WaitForAssertion(() => Assert.IsTrue(posts.Contains("/settings/smtp"),
            "the SMTP save uses its own endpoint, not /settings"));
        Assert.IsFalse(posts.Contains("/settings"), "branding endpoint not hit by the SMTP save");
    }

    private sealed class Handler(SiteSettingsViewModel vm, List<string> postPaths) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
                postPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(vm, Json), Encoding.UTF8, "application/json"),
            });
        }
    }
}
