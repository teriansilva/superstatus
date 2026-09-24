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

    /// <summary>#432: a handler whose <c>/statuscheck/providers</c> response can be held open,
    /// so the render-ordering race is reproducible here too. The synchronous handler above
    /// completes without ever yielding, which is exactly the timing that HIDES this bug — the
    /// first render already has the descriptors, so the selector is never built empty.</summary>
    private sealed class DeferredProvidersHandler(Task<HttpResponseMessage> providers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => request.RequestUri!.AbsolutePath switch
            {
                "/statuscheck/providers" => providers,
                "/admin/slas" or "/admin/webhooks" or "/admin/alert-profiles" => Task.FromResult(Json("[]")),
                _ => Task.FromResult(Json("{}")),
            };
    }

    private static BunitTestContext CtxWithDeferredProviders(Task<HttpResponseMessage> providers)
    {
        var ctx = new BunitTestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new DeferredProvidersHandler(providers)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return ctx;
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

    /// <summary>
    /// #432: the batch dialog's Type selector has the same render-ordering race as the edit
    /// dialog's, and needs its own guard — the edit-dialog test cannot cover this second
    /// production surface. Against the real API `/statuscheck/providers` is a network round
    /// trip, so the first render has an empty provider list and a selector built then has no
    /// `MudSelectItem` to paint from. The gate defers building it until the descriptors land.
    /// </summary>
    [TestMethod]
    public async Task TypeSelector_WhenDescriptorsArriveAfterTheFirstRender_PaintsTheDisplayName()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctx = CtxWithDeferredProviders(release.Task);
        var provider = await Open(ctx);

        // Before the descriptors land the selector must not exist at all — an empty-but-present
        // Type box is the defect, so "absent" is the correct intermediate state.
        Assert.AreEqual(0, provider.FindAll(".chk-batch-type").Count,
            "the selector is not built while the provider list is still empty");

        release.SetResult(Json(ProvidersJson));

        provider.WaitForAssertion(() =>
        {
            var painted = provider.Find(".chk-batch-type div.mud-input-slot").TextContent.Trim();
            Assert.AreNotEqual(string.Empty, painted, "the Type box must never be painted empty");
            Assert.AreEqual("HTTP(S)", painted,
                "the default batch-capable provider paints its DisplayName, not its raw TypeId");
        });
    }
}
