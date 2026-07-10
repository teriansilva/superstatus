using System.Net;
using System.Text;
using System.Text.Json;
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
/// Issue #367 — the Email plugin settings modal: renders the SMTP relay form,
/// masks a stored password, saves to the dedicated /settings/smtp endpoint, and
/// reuses the existing test-send endpoint.
/// </summary>
[TestClass]
public class SmtpPanelUiTests
{
    private static BunitTestContext Ctx(SiteSettingsViewModel vm, List<string> postPaths)
    {
        var ctx = new BunitTestContext();
        var client = new SettingsApiClient(new HttpClient(new Handler(vm, postPaths)) { BaseAddress = new Uri("http://api.test") });
        ctx.Services.AddSingleton(client);
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static async Task<IRenderedComponent<MudDialogProvider>> Open(BunitTestContext ctx)
    {
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => service.ShowAsync<EmailPluginSettingsDialog>("email"));
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("EMAIL SETTINGS")));
        return provider;
    }

    [TestMethod]
    public async Task DialogRendersSmtpSection_MaskedPasswordWhenStored()
    {
        var vm = new SiteSettingsViewModel { SmtpHost = "mail.test", SmtpFromAddress = "alerts@test", SmtpPasswordSet = true };
        using var ctx = Ctx(vm, new());
        var provider = await Open(ctx);
        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Markup.Contains("SMTP relay"));
            Assert.IsTrue(provider.Markup.Contains("mail.test"), "host shown");
            Assert.IsTrue(provider.Markup.Contains("(stored)"), "stored password is masked, not echoed");
            Assert.IsTrue(provider.Markup.Contains("Save email settings"));
            Assert.IsTrue(provider.Markup.Contains("Send test email"));
        });
    }

    [TestMethod]
    public async Task SaveEmailSettings_PostsToDedicatedSmtpEndpoint()
    {
        var posts = new List<string>();
        using var ctx = Ctx(new SiteSettingsViewModel(), posts);
        var provider = await Open(ctx);

        var saveSmtp = provider.FindAll("button").First(b => b.TextContent.Contains("Save email settings"));
        saveSmtp.Click();

        provider.WaitForAssertion(() => Assert.IsTrue(posts.Contains("/settings/smtp"),
            "the SMTP save uses its own endpoint, not /settings"));
        Assert.IsFalse(posts.Contains("/settings"), "branding endpoint not hit by the SMTP save");
    }

    [TestMethod]
    public async Task SendTestEmail_PostsToExistingTestEndpoint()
    {
        var posts = new List<string>();
        using var ctx = Ctx(new SiteSettingsViewModel(), posts);
        var provider = await Open(ctx);

        provider.FindAll("button").First(b => b.TextContent.Contains("Send test email")).Click();

        provider.WaitForAssertion(() => Assert.IsTrue(posts.Contains("/admin/email/test"),
            "the modal reuses the existing email test endpoint"));
    }

    private sealed class Handler(SiteSettingsViewModel vm, List<string> postPaths) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
                postPaths.Add(request.RequestUri!.AbsolutePath);
            var content = request.RequestUri!.AbsolutePath == "/admin/email/test"
                ? JsonSerializer.Serialize(new { ok = true, target = "ops@test" }, Json)
                : JsonSerializer.Serialize(vm, Json);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }
}
