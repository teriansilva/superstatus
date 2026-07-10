using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.StatusCheckOverview;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Epic #342 (batch add) — the batch-add dialog: it offers only batch-capable providers
/// (the push/heartbeat provider, which declares no target field, is excluded), hides the
/// per-target field from the shared config, renders the live parse preview (valid /
/// duplicate / invalid counts), and disables submit until there is at least one valid
/// target. The server-side create is covered by <see cref="BatchCheckCreationTests"/>.
/// </summary>
[TestClass]
public class BatchAddChecksDialogTests
{
    // http + ai declare a batchTargetField; heartbeat (push) does not — so the dialog
    // must offer http/ai and exclude heartbeat.
    private const string ProvidersJson = """
    [
      {"typeId":"http","displayName":"HTTP(S)","icon":"link","schemaVersion":1,"direction":"pull","batchTargetField":"url",
       "fields":[
         {"key":"url","label":"URL","kind":"text","required":true,"options":[]},
         {"key":"expectedStatusCode","label":"Expected status","kind":"number","required":true,"options":[]}],
       "metrics":[]},
      {"typeId":"ai","displayName":"AI / LLM endpoint","icon":"sparkle","schemaVersion":1,"direction":"pull","batchTargetField":"baseUrl",
       "fields":[
         {"key":"baseUrl","label":"Base URL","kind":"text","required":true,"options":[]},
         {"key":"model","label":"Model","kind":"text","required":true,"options":[]}],
       "metrics":[]},
      {"typeId":"heartbeat","displayName":"Agent heartbeat","icon":"pulse","schemaVersion":1,"direction":"push","batchTargetField":null,
       "fields":[{"key":"intervalSeconds","label":"Expected interval (s)","kind":"number","required":true,"options":[]}],
       "metrics":[]}
    ]
    """;

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RoutingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = request.RequestUri!.AbsolutePath switch
            {
                "/statuscheck/providers" => Json(ProvidersJson),
                "/admin/slas" => Json("[]"),
                "/admin/webhooks" => Json("[]"),
                "/admin/alert-profiles" => Json("[]"),
                _ => Json("{}"),
            };
            return Task.FromResult(resp);
        }
    }

    private static BunitTestContext Ctx()
    {
        var ctx = new BunitTestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new RoutingHandler()) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return ctx;
    }

    private static async Task<IRenderedComponent<MudDialogProvider>> Open(BunitTestContext ctx, string? seedTargets = null)
    {
        ctx.RenderComponent<MudPopoverProvider>();
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<BatchAddChecksDialog>();
        if (seedTargets is not null) parameters.Add(x => x.SeedTargets, seedTargets);
        await provider.InvokeAsync(() => service.ShowAsync<BatchAddChecksDialog>("batch", parameters));
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("BATCH ADD")));
        return provider;
    }

    [TestMethod]
    public async Task Dialog_ExcludesPushProviderFromTypeSelector()
    {
        using var ctx = Ctx();
        var provider = await Open(ctx);

        provider.WaitForAssertion(() =>
        {
            // http is the default-selected batch-capable provider…
            StringAssert.Contains(provider.Markup, "HTTP(S)");
            // …and the push provider (no target field) is not offered at all.
            Assert.IsFalse(provider.Markup.Contains("Agent heartbeat"),
                "heartbeat has no BatchTargetField and must be excluded from the batch dialog");
        });
    }

    [TestMethod]
    public async Task Dialog_HidesTargetField_FromSharedConfig()
    {
        using var ctx = Ctx();
        var provider = await Open(ctx);

        provider.WaitForAssertion(() =>
        {
            // The per-target field (url) is filled from the paste, so it must NOT appear
            // as a shared config input; the other schema field (expected status) does.
            Assert.AreEqual(0, provider.FindAll(".chk-batch-cfg-url").Count);
            StringAssert.Contains(provider.Markup, "Expected status");
        });
    }

    [TestMethod]
    public async Task Dialog_Preview_CountsValidDuplicateInvalid()
    {
        using var ctx = Ctx();
        // 2 valid, 1 duplicate, 1 invalid (no host).
        var seed = "https://web.example.com/health\nhttps://api.example.com/healthz\nhttps://web.example.com/health\nhttp://";
        var provider = await Open(ctx, seed);

        provider.WaitForAssertion(() =>
        {
            var summary = provider.Find(".chk-batch-summary").TextContent;
            StringAssert.Contains(summary, "2 valid");
            StringAssert.Contains(summary, "1 duplicate");
            StringAssert.Contains(summary, "1 invalid");
            // The submit button counts only the valid targets.
            StringAssert.Contains(provider.Find(".chk-batch-submit").TextContent, "Create 2 checks");
        });
    }

    [TestMethod]
    public async Task Dialog_SubmitDisabled_WhenNoValidTargets()
    {
        using var ctx = Ctx();
        var provider = await Open(ctx); // empty paste

        provider.WaitForAssertion(() =>
        {
            var submit = provider.Find(".chk-batch-submit");
            Assert.IsTrue(submit.HasAttribute("disabled"), "submit is disabled with zero valid targets");
        });
    }
}
