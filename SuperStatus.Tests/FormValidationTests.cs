using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SuperStatus.Data.ViewModels;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using SuperStatus.Web.Components.IncidentOverview;
using SuperStatus.Web.Components.StatusCheckOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #389 — the plain-input settings/onboarding forms used to save
/// unconditionally and lean entirely on server sanitize/clamp, so an invalid or
/// incomplete config saved "successfully" and silently misbehaved. These tests pin
/// the client-side gates: a blank/invalid config must NOT reach the API (no POST),
/// a valid one still does, numeric-kind fields reject non-numbers, and a failed
/// incident save surfaces a snackbar instead of tearing the Blazor circuit.
/// </summary>
[TestClass]
public class FormValidationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static HttpResponseMessage Ok(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---------------------------------------------------------------------
    // 1. SiteSettingsPanel — AI enable can't silently save a no-op config.
    // ---------------------------------------------------------------------

    // Serves the settings VM on GET /settings and records every POST path.
    private sealed class SettingsHandler(SiteSettingsViewModel vm, List<string> posts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post) posts.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Ok(JsonSerializer.Serialize(vm, Json)));
        }
    }

    private static BunitTestContext SettingsCtx(SiteSettingsViewModel vm, List<string> posts)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new SettingsHandler(vm, posts)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new SettingsApiClient(http));
        ctx.Services.AddSingleton(new IssuerModeInfo(isDynamic: false));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [TestMethod]
    public void SiteSettings_EnableAiWithBlankConfig_DoesNotSave()
    {
        var posts = new List<string>();
        // Operator opens settings, ticks "Enable AI-authored incidents", leaves the
        // base URL + model blank, and saves. The server would silently drop the
        // enablement — so the client must block the POST and say why.
        using var ctx = SettingsCtx(new SiteSettingsViewModel { AccentColor = "#3fbf6f" }, posts);
        var cut = ctx.RenderComponent<SiteSettingsPanel>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Enable AI-authored incidents")));

        var aiToggle = cut.FindAll("label.settings-check")
            .First(l => l.TextContent.Contains("Enable AI-authored incidents"))
            .QuerySelector("input[type=checkbox]")!;
        aiToggle.Change(true);

        cut.Find(".settings-actions button.primary").Click();

        Assert.IsFalse(posts.Contains("/settings"),
            "AI enabled with a blank base URL/model must not POST — the save is blocked client-side.");
    }

    [TestMethod]
    public void SiteSettings_EnableAiWithFullConfig_Saves()
    {
        var posts = new List<string>();
        // AI on, endpoint + model present, ranges valid → the save goes through.
        var vm = new SiteSettingsViewModel
        {
            AccentColor = "#3fbf6f",
            AiEnabled = true,
            AiBaseUrl = "https://gateway.test/v1",
            AiModel = "gpt-4o-mini",
            AiTimeoutSeconds = 20,
            AutoIncidentThresholdMinutes = 5,
        };
        using var ctx = SettingsCtx(vm, posts);
        var cut = ctx.RenderComponent<SiteSettingsPanel>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Save settings")));
        cut.Find(".settings-actions button.primary").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(posts.Contains("/settings"),
            "a complete, in-range AI config saves normally."));
    }

    [TestMethod]
    public void ValidateAi_CoversBlankConfigAndRanges()
    {
        // Disabled → never blocks, whatever the other fields hold.
        Assert.IsNull(SiteSettingsPanel.ValidateAi(new SiteSettingsViewModel { AiEnabled = false, AiTimeoutSeconds = 9999 }));
        // Enabled but blank endpoint/model → blocked.
        Assert.IsNotNull(SiteSettingsPanel.ValidateAi(new SiteSettingsViewModel { AiEnabled = true, AiBaseUrl = "", AiModel = "m" }));
        Assert.IsNotNull(SiteSettingsPanel.ValidateAi(new SiteSettingsViewModel { AiEnabled = true, AiBaseUrl = "https://x/v1", AiModel = "" }));
        // Enabled + endpoint/model present but a numeric field out of range → blocked.
        Assert.IsNotNull(SiteSettingsPanel.ValidateAi(new SiteSettingsViewModel { AiEnabled = true, AiBaseUrl = "https://x/v1", AiModel = "m", AiTimeoutSeconds = 4 }));
        Assert.IsNotNull(SiteSettingsPanel.ValidateAi(new SiteSettingsViewModel { AiEnabled = true, AiBaseUrl = "https://x/v1", AiModel = "m", AiTimeoutSeconds = 121 }));
        Assert.IsNotNull(SiteSettingsPanel.ValidateAi(new SiteSettingsViewModel { AiEnabled = true, AiBaseUrl = "https://x/v1", AiModel = "m", AiTimeoutSeconds = 20, AutoIncidentThresholdMinutes = 0 }));
        Assert.IsNotNull(SiteSettingsPanel.ValidateAi(new SiteSettingsViewModel { AiEnabled = true, AiBaseUrl = "https://x/v1", AiModel = "m", AiTimeoutSeconds = 20, AutoIncidentThresholdMinutes = 1441 }));
        // Fully valid → allowed.
        Assert.IsNull(SiteSettingsPanel.ValidateAi(new SiteSettingsViewModel { AiEnabled = true, AiBaseUrl = "https://x/v1", AiModel = "m", AiTimeoutSeconds = 20, AutoIncidentThresholdMinutes = 5 }));
    }

    // ---------------------------------------------------------------------
    // 2. EmailPluginSettingsDialog — SMTP can't save blank host/from.
    // ---------------------------------------------------------------------

    private static async Task<IRenderedComponent<MudDialogProvider>> OpenEmail(BunitTestContext ctx)
    {
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => service.ShowAsync<EmailPluginSettingsDialog>("email"));
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("EMAIL SETTINGS")));
        return provider;
    }

    [TestMethod]
    public async Task Smtp_SaveWithBlankHost_DoesNotPost()
    {
        var posts = new List<string>();
        // From-address present, host blank → the relay is unusable, so no POST.
        var vm = new SiteSettingsViewModel { SmtpHost = "", SmtpFromAddress = "alerts@test", SmtpPort = 587 };
        var ctx = SettingsCtx(vm, posts);
        using var _ = ctx;
        var provider = await OpenEmail(ctx);

        provider.FindAll("button").First(b => b.TextContent.Contains("Save email settings")).Click();

        Assert.IsFalse(posts.Contains("/settings/smtp"),
            "a blank SMTP host blocks the save client-side — no POST.");
    }

    [TestMethod]
    public void ValidateSmtp_CoversRequiredAndPortRange()
    {
        Assert.IsNotNull(EmailPluginSettingsDialog.ValidateSmtp(new SiteSettingsViewModel { SmtpHost = "", SmtpFromAddress = "a@b", SmtpPort = 587 }));
        Assert.IsNotNull(EmailPluginSettingsDialog.ValidateSmtp(new SiteSettingsViewModel { SmtpHost = "mail.test", SmtpFromAddress = "", SmtpPort = 587 }));
        Assert.IsNotNull(EmailPluginSettingsDialog.ValidateSmtp(new SiteSettingsViewModel { SmtpHost = "mail.test", SmtpFromAddress = "a@b", SmtpPort = 0 }));
        Assert.IsNotNull(EmailPluginSettingsDialog.ValidateSmtp(new SiteSettingsViewModel { SmtpHost = "mail.test", SmtpFromAddress = "a@b", SmtpPort = 70000 }));
        Assert.IsNull(EmailPluginSettingsDialog.ValidateSmtp(new SiteSettingsViewModel { SmtpHost = "mail.test", SmtpFromAddress = "a@b", SmtpPort = 587 }));
    }

    // ---------------------------------------------------------------------
    // 3. IncidentEditDialog — a server error surfaces a snackbar, not a torn circuit.
    // ---------------------------------------------------------------------

    private sealed class FailingIncidentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
            });
    }

    [TestMethod]
    public async Task Incident_SaveFailure_ShowsSnackbarAndStaysOpen()
    {
        var ctx = new BunitTestContext();
        using var _ = ctx;
        var http = new HttpClient(new FailingIncidentHandler()) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new IncidentApiClient(http));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        ctx.RenderComponent<MudPopoverProvider>();
        var snackbars = ctx.RenderComponent<MudSnackbarProvider>();
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();

        var parameters = new DialogParameters<IncidentEditDialog>
        {
            { x => x.Incident, new IncidentViewModel { Title = "Payments outage" } },
        };
        await provider.InvokeAsync(() => service.ShowAsync<IncidentEditDialog>("incident", parameters));
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("INCIDENT")));

        // The save (POST /incidents/edit → 500) must NOT throw into the circuit.
        provider.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        snackbars.WaitForAssertion(() => Assert.IsTrue(snackbars.Markup.Contains("Could not save incident"),
            "a failed save surfaces a snackbar instead of tearing the Blazor circuit."));
        Assert.IsTrue(provider.Markup.Contains("INCIDENT"), "the dialog stays open so the operator can retry.");
    }

    // ---------------------------------------------------------------------
    // 4. BatchAddChecksDialog — a non-numeric `number` field blocks submit.
    // ---------------------------------------------------------------------

    private const string BatchProvidersJson = """
    [
      {"typeId":"http","displayName":"HTTP(S)","icon":"link","schemaVersion":1,"direction":"pull","batchTargetField":"url",
       "fields":[
         {"key":"url","label":"URL","kind":"text","required":true,"options":[]},
         {"key":"expectedStatusCode","label":"Expected status","kind":"number","required":true,"options":[]}],
       "metrics":[]}
    ]
    """;

    private sealed class BatchHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/statuscheck/providers" => Ok(BatchProvidersJson),
                _ => Ok("[]"),
            });
    }

    [TestMethod]
    public async Task Batch_NonNumericExpectedStatus_BlocksSubmit()
    {
        var ctx = new BunitTestContext();
        using var _ = ctx;
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new BatchHandler()) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));

        ctx.RenderComponent<MudPopoverProvider>();
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<BatchAddChecksDialog> { { x => x.SeedTargets, "https://web.example.com/health" } };
        await provider.InvokeAsync(() => service.ShowAsync<BatchAddChecksDialog>("batch", parameters));
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("Expected status")));

        // With one valid target and the default "200", submit is enabled…
        provider.WaitForAssertion(() => Assert.IsFalse(provider.Find(".chk-batch-submit").HasAttribute("disabled")));

        // …type a non-number into the shared numeric field → submit blocks + error shows.
        provider.Find(".chk-batch-cfg-expectedStatusCode input").Change("abc");

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(provider.Find(".chk-batch-submit").HasAttribute("disabled"),
                "a non-numeric shared number field disables submit.");
            StringAssert.Contains(provider.Find(".chk-batch-error").TextContent, "must be a number");
        });
    }

    // ---------------------------------------------------------------------
    // 5. SetupWizard — a half-filled first check warns instead of vanishing.
    // ---------------------------------------------------------------------

    // GET /settings → empty settings; records every POST path so we can assert
    // whether the onboarding check was (or wasn't) created.
    private sealed class WizardHandler(List<string> posts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post) posts.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Ok("{}"));
        }
    }

    private static BunitTestContext WizardCtx(List<string> posts)
    {
        var ctx = new BunitTestContext();
        ctx.AddTestAuthorization().SetAuthorized("operator");
        ctx.Services.AddMudServices();
        var http = new HttpClient(new WizardHandler(posts)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        ctx.Services.AddSingleton(new SettingsApiClient(http));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // Steps 0 (Welcome) → 1 (Branding) → 2 (First status check).
    private static void AdvanceToCheckStep(IRenderedComponent<SetupWizard> cut)
    {
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Step 1 of 6")));
        cut.Find("button.btn.primary").Click(); // Welcome → Branding
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Step 2 of 6")));
        cut.Find("button.btn.primary").Click(); // Branding → First status check (POST /settings)
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Step 3 of 6")));
    }

    [TestMethod]
    public void Wizard_Step2_PartiallyFilled_WarnsAndDoesNotAdvanceOrCreate()
    {
        var posts = new List<string>();
        using var ctx = WizardCtx(posts);
        var cut = ctx.RenderComponent<SetupWizard>();
        AdvanceToCheckStep(cut);

        // Fill only the service name — leave the URL blank.
        cut.Find(".setup-grid input[type=text]").Change("Public API");
        cut.Find("button.btn.primary").Click();

        Assert.IsTrue(cut.Markup.Contains("Step 3 of 6"),
            "a half-filled check keeps the operator on the step instead of silently advancing.");
        Assert.IsFalse(posts.Contains("/statuscheck/edit"),
            "the half-filled check is NOT created — no quiet data loss, no partial save.");
    }

    [TestMethod]
    public void Wizard_Step2_BothBlank_SkipsWithoutCreating()
    {
        var posts = new List<string>();
        using var ctx = WizardCtx(posts);
        var cut = ctx.RenderComponent<SetupWizard>();
        AdvanceToCheckStep(cut);

        // Leave both blank → the documented "skip" path: advance, create nothing.
        cut.Find("button.btn.primary").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Step 4 of 6"), "both-blank advances (skip)."));
        Assert.IsFalse(posts.Contains("/statuscheck/edit"), "nothing is created when the step is skipped.");
    }

    [TestMethod]
    public void Wizard_Step2_BothFilled_CreatesAndAdvances()
    {
        var posts = new List<string>();
        using var ctx = WizardCtx(posts);
        var cut = ctx.RenderComponent<SetupWizard>();
        AdvanceToCheckStep(cut);

        cut.Find(".setup-grid input[type=text]").Change("Public API");
        cut.Find(".setup-grid input[type=url]").Change("https://api.example.com/health");
        cut.Find("button.btn.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Step 4 of 6"), "a complete check advances.");
            Assert.IsTrue(posts.Contains("/statuscheck/edit"), "a complete check is created.");
        });
    }
}
