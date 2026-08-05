using System.Net;
using System.Text;
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
/// #433 — the guided AI setup and the generic dry-run action in <see cref="StatusCheckEditDialog"/>.
///
/// The behaviour worth pinning is the state machine, not the markup: what is revealed when, that
/// a failed validate never traps the operator, that a typed model survives re-validation, and —
/// the subtle one — that a slow reply cannot land on state it no longer describes.
/// </summary>
[TestClass]
public class AiGuidedSetupDialogTests
{
    private const string ProvidersJson = """
    [
      {"typeId":"http","displayName":"HTTP(S)","icon":"link","schemaVersion":1,"direction":"pull","fields":[
        {"key":"url","label":"URL","kind":"text","required":true},
        {"key":"expectedStatusCode","label":"Expected status","kind":"number","required":true}
      ]},
      {"typeId":"ai","displayName":"AI / LLM endpoint","icon":"robot","schemaVersion":1,"direction":"pull","fields":[
        {"key":"baseUrl","label":"Base URL","kind":"text","required":true},
        {"key":"apiKey","label":"API key","kind":"secret","required":false},
        {"key":"model","label":"Model","kind":"text","required":true},
        {"key":"prompt","label":"Canary prompt","kind":"text","required":true},
        {"key":"expectContains","label":"Response must contain","kind":"text","required":true}
      ]},
      {"typeId":"heartbeat","displayName":"Agent heartbeat","icon":"pulse","schemaVersion":1,"direction":"push","fields":[
        {"key":"graceSeconds","label":"Grace (s)","kind":"number","required":true}
      ]}
    ]
    """;

    private sealed record Recorded(string Method, string Path, string? Body);

    /// <summary>Captures the <see cref="CancellationToken"/> each call was made with, so a test
    /// can assert the dialog actually cancels outstanding work rather than merely ignoring its
    /// reply.</summary>
    private sealed class TokenCapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public CancellationToken LastToken { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastToken = ct;
            return await responder(request);
        }
    }

    private static (BunitTestContext ctx, TokenCapturingHandler handler) CtxCapturingToken(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var ctx = new BunitTestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new TokenCapturingHandler(responder);
        ctx.Services.AddSingleton(new StatusApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test") }));
        return (ctx, handler);
    }

    private sealed class RoutingHandler(List<Recorded> requests, Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            requests.Add(new Recorded(request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return await responder(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (BunitTestContext ctx, List<Recorded> requests) Ctx(Func<HttpRequestMessage, Task<HttpResponseMessage>>? responder = null)
    {
        var ctx = new BunitTestContext();
        var requests = new List<Recorded>();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new RoutingHandler(requests, responder ?? (r => Task.FromResult(Default(r)))))
        {
            BaseAddress = new Uri("http://api.test"),
        };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return (ctx, requests);
    }

    private static HttpResponseMessage Default(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/statuscheck/providers" => Json(ProvidersJson),
        "/admin/slas" => Json("[]"),
        "/admin/webhooks" => Json("[]"),
        "/admin/alert-profiles" => Json("[]"),
        _ => Json("{}"),
    };

    private static async Task<IRenderedComponent<MudDialogProvider>> Open(BunitTestContext ctx, StatusCheckViewModelBase vm)
    {
        ctx.RenderComponent<MudPopoverProvider>();
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => service.ShowAsync<StatusCheckEditDialog>("check",
            new DialogParameters<StatusCheckEditDialog> { { x => x.StatusCheck, vm } }));
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("ADD CHECK") || provider.Markup.Contains("EDIT CHECK")));
        return provider;
    }

    private static StatusCheckViewModelBase NewAiCheck() => new()
    {
        Title = "Inference gateway",
        ProviderType = "ai",
        IntervalSeconds = 60,
        Enabled = true,
        StatusCheckUrl = string.Empty,
        ServiceLogoUrl = string.Empty,
    };

    /// <summary>Set a schema-driven config field by invoking the component's own
    /// <c>ValueChanged</c>. A DOM <c>.Change()</c> does not reliably reach a MudTextField bound
    /// via Value/ValueChanged in this dialog, and a silently-ignored edit would make a fencing
    /// test pass for the wrong reason — it would be asserting that nothing changed rather than
    /// that a stale reply was rejected. `StatusCheckEditDialogTests` drives fields the same way.</summary>
    private static void Type(IRenderedComponent<MudDialogProvider> provider, string cssClass, string value)
    {
        var field = provider.FindComponents<MudTextField<string>>()
            .First(f => (f.Instance.Class ?? "").Contains(cssClass));
        provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value)).GetAwaiter().GetResult();
    }

    // ---- staging -------------------------------------------------------------

    [TestMethod]
    public async Task NewAiCheck_AsksOnlyForTheEndpointFirst_AndDefersTheRest()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var provider = await Open(ctx, NewAiCheck());

        provider.WaitForAssertion(() =>
        {
            // Stage 1: the two fields that identify the endpoint.
            provider.Find(".chk-cfg-baseUrl");
            provider.Find(".chk-cfg-apiKey");
            provider.Find(".chk-ai-validate-btn");

            // Everything downstream of "which models exist" is still collapsed, and the dialog
            // says why rather than just hiding things.
            Assert.AreEqual(0, provider.FindAll(".chk-cfg-model").Count, "Model is deferred until the endpoint answers");
            Assert.AreEqual(0, provider.FindAll(".chk-cfg-prompt").Count);
            StringAssert.Contains(provider.Find(".chk-ai-deferred").TextContent, "until the endpoint has answered");
        });
    }

    [TestMethod]
    public async Task ExistingAiCheckWithAModel_IsNeverCollapsed()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.Id = 12;
        vm.ProviderConfig["baseUrl"] = "https://ai.example/v1";
        vm.ProviderConfig["model"] = "gpt-4o-mini";

        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() =>
        {
            provider.Find(".chk-cfg-model");
            provider.Find(".chk-cfg-prompt");
            Assert.AreEqual(0, provider.FindAll(".chk-ai-deferred").Count,
                "a configured check is never hidden behind a re-validation");
        });
    }

    [TestMethod]
    public async Task ANonAiProvider_KeepsTheFlatSchemaForm_WithNoValidateStep()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.ProviderType = "http";

        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() =>
        {
            provider.Find(".chk-cfg-url");
            provider.Find(".chk-cfg-expectedStatusCode");
            Assert.AreEqual(0, provider.FindAll(".chk-ai-validate-btn").Count, "staging is AI-only");
        });
    }

    // ---- validate ------------------------------------------------------------

    [TestMethod]
    public async Task Validate_DiscoversModels_ThenOffersThemAsAPicker()
    {
        var (ctx, requests) = Ctx(r => Task.FromResult(r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? Json("""{"ok":true,"supportsListing":true,"models":["gpt-4.1","gpt-4o-mini"],"message":"Connected — 2 models."}""")
            : Default(r)));
        using var _ctx = ctx;
        var provider = await Open(ctx, NewAiCheck());

        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://ai.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();

        provider.WaitForAssertion(() =>
        {
            StringAssert.Contains(provider.Find(".chk-ai-vstate").TextContent, "Connected");
            provider.Find(".chk-ai-model-select");                  // Model is now a picker
            provider.Find(".chk-cfg-prompt");                        // …and the rest is revealed
        });
        Assert.IsTrue(requests.Any(r => r is { Method: "POST", Path: "/statuscheck/providers/ai/models" }));
    }

    [TestMethod]
    public async Task AnEndpointWithNoModelList_FallsBackToFreeText_AndStillRevealsTheRest()
    {
        var (ctx, _) = Ctx(r => Task.FromResult(r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? Json("""{"ok":true,"supportsListing":false,"models":[],"message":"Reachable, but this endpoint doesn't list models — type the model id."}""")
            : Default(r)));
        using var _ctx = ctx;
        var provider = await Open(ctx, NewAiCheck());

        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://gateway.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();

        provider.WaitForAssertion(() =>
        {
            StringAssert.Contains(provider.Find(".chk-ai-vstate").TextContent, "no model list");
            provider.Find(".chk-cfg-model");                                     // plain text field
            Assert.AreEqual(0, provider.FindAll(".chk-ai-model-select").Count, "no picker without a list");
            provider.Find(".chk-cfg-prompt");                                    // never a dead end
        });
    }

    /// <summary>A failed validate must not trap the operator: the escape hatch reveals every
    /// field so the check can still be configured and saved by hand.</summary>
    [TestMethod]
    public async Task AFailedValidate_StillLetsTheOperatorConfigureManually()
    {
        var (ctx, _) = Ctx(r => Task.FromResult(r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? Json("""{"ok":false,"supportsListing":false,"models":[],"message":"Could not reach the endpoint."}""")
            : Default(r)));
        using var _ctx = ctx;
        var provider = await Open(ctx, NewAiCheck());

        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://unreachable.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();

        // A failed attempt still counts as "the endpoint answered" for reveal purposes, so the
        // operator can carry on; the verdict tells them it didn't work.
        provider.WaitForAssertion(() =>
        {
            StringAssert.Contains(provider.Find(".chk-ai-vstate").TextContent, "Could not reach");
            provider.Find(".chk-cfg-model");
        });
    }

    [TestMethod]
    public async Task ReValidating_KeepsAModelTheOperatorAlreadyTyped()
    {
        var (ctx, _) = Ctx(r => Task.FromResult(r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? Json("""{"ok":true,"supportsListing":true,"models":["gpt-4.1"],"message":"Connected — 1 model."}""")
            : Default(r)));
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.Id = 3;
        vm.ProviderConfig["baseUrl"] = "https://ai.example/v1";
        vm.ProviderConfig["model"] = "my-private-deployment";   // not in the endpoint's list

        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => provider.Find(".chk-ai-validate-btn"));
        provider.Find(".chk-ai-validate-btn").Click();

        provider.WaitForAssertion(() =>
        {
            StringAssert.Contains(provider.Find(".chk-ai-model-select").TextContent, "my-private-deployment");
            Assert.AreEqual("my-private-deployment", vm.ProviderConfig["model"],
                "re-validating must never silently swap the operator's model for one of the endpoint's");
        });
    }

    /// <summary>
    /// THE FENCE. A slow reply must not land on state it no longer describes. Here validate is
    /// in flight when the operator switches provider; when it finally returns, its model list
    /// must be discarded rather than applied to a provider that has no models at all.
    /// </summary>
    [TestMethod]
    public async Task ASlowValidateReply_IsDiscardedWhenTheProviderChangedMeanwhile()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, _) = Ctx(r => r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? release.Task
            : Task.FromResult(Default(r)));
        using var _ctx = ctx;
        var vm = NewAiCheck();
        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://ai.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();                 // in flight, not yet answered

        // Operator gives up waiting and switches to HTTP.
        var select = provider.FindComponents<MudSelect<string>>()
            .Single(s => s.Instance.Class is not null && s.Instance.Class.Contains("chk-provider-type"));
        await provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("http"));
        provider.WaitForAssertion(() => provider.Find(".chk-cfg-url"));

        // …and only now does the AI endpoint answer.
        release.SetResult(Json("""{"ok":true,"supportsListing":true,"models":["gpt-4.1"],"message":"Connected — 1 model."}"""));

        // Positive signal first: the http provider's own fields are rendered and settled.
        provider.WaitForAssertion(() => provider.Find(".chk-cfg-expectedStatusCode"));

        Assert.AreEqual(0, provider.FindAll(".chk-ai-model-select").Count,
            "a stale reply must not inject a model picker into a provider that has no models");
        Assert.AreEqual(0, provider.FindAll(".chk-ai-vstate").Count,
            "…nor leave the previous provider's verdict on screen");
        provider.Find(".chk-cfg-url");
    }

    // ---- test connection -----------------------------------------------------

    [TestMethod]
    public async Task TestConnection_PostsTheTypedConfig_AndRendersTheVerdictWithMetrics()
    {
        var (ctx, requests) = Ctx(r => Task.FromResult(r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/test"
            ? Json("""{"outcome":"up","ok":true,"latencyMs":988,"metrics":[{"key":"ttft_ms","label":"TTFT","unit":"ms","value":412}]}""")
            : Default(r)));
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.Id = 9;
        // Every required field is filled — Test is gated on schema completeness now, so a
        // partial fixture would be asserting against a disabled button.
        vm.ProviderConfig["baseUrl"] = "https://ai.example/v1";
        vm.ProviderConfig["model"] = "gpt-4o-mini";
        vm.ProviderConfig["prompt"] = "Reply with the single word: pong";
        vm.ProviderConfig["expectContains"] = "pong";

        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => provider.Find(".chk-test-btn"));
        provider.Find(".chk-test-btn").Click();

        provider.WaitForAssertion(() =>
        {
            var verdict = provider.Find(".chk-test-verdict");
            StringAssert.Contains(verdict.TextContent, "up");
            StringAssert.Contains(verdict.TextContent, "TTFT");
            StringAssert.Contains(verdict.TextContent, "412");
            StringAssert.Contains(verdict.TextContent, "not saved", "a dry run says plainly that it committed nothing");
        });

        var posted = requests.Single(r => r is { Method: "POST", Path: "/statuscheck/providers/ai/test" });
        StringAssert.Contains(posted.Body!, "gpt-4o-mini");
        Assert.IsFalse(requests.Any(r => r.Path == "/statuscheck/edit"), "testing must never save");
    }

    [TestMethod]
    public async Task TestConnection_IsHiddenForAPushProvider()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.ProviderType = "heartbeat";

        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() =>
            Assert.AreEqual(0, provider.FindAll(".chk-test-btn").Count,
                "a push check has no target to reach out to, so there is nothing to test"));
    }

    [TestMethod]
    public async Task TestConnection_SurfacesAServerRejectionAsADownVerdict()
    {
        var (ctx, _) = Ctx(r => Task.FromResult(r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/test"
            ? Json("""{"message":"AI / LLM endpoint: missing required 'Model'."}""", HttpStatusCode.UnprocessableEntity)
            : Default(r)));
        using var _ctx = ctx;
        var vm = NewAiCheck();
        // Client-side complete, so the button is enabled and the request is actually made —
        // the point of this test is how a SERVER-side rejection is surfaced.
        vm.ProviderConfig["baseUrl"] = "https://ai.example/v1";
        vm.ProviderConfig["model"] = "x";
        vm.ProviderConfig["prompt"] = "ping";
        vm.ProviderConfig["expectContains"] = "pong";

        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => provider.Find(".chk-test-btn"));
        provider.Find(".chk-test-btn").Click();

        provider.WaitForAssertion(() =>
        {
            var verdict = provider.Find(".chk-test-verdict");
            StringAssert.Contains(verdict.TextContent, "missing required");
            Assert.IsTrue(verdict.ClassList.Contains("down"));
        });
    }

    // ---- #435 review: fencing, busy-state and the Test gate ------------------

    /// <summary>
    /// A generation counter alone does not fence this: editing Base URL goes through
    /// <c>SetCfg</c>, which starts no request and advances no counter. So a reply about
    /// endpoint A must be rejected because the CONFIG it was asked about no longer matches —
    /// otherwise the dialog reports B as connected while showing A's models.
    /// </summary>
    [TestMethod]
    public async Task ASlowValidateReply_IsDiscardedWhenTheBaseUrlChangedMeanwhile()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, _) = Ctx(r => r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? release.Task
            : Task.FromResult(Default(r)));
        using var _ctx = ctx;
        var vm = NewAiCheck();
        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://a.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();

        // The operator retypes the endpoint while A's answer is still on the wire.
        Type(provider, "chk-cfg-baseUrl", "https://b.example/v1");
        Assert.AreEqual("https://b.example/v1", vm.ProviderConfig["baseUrl"],
            "sanity: the edit must actually have landed, or this test proves nothing");
        release.SetResult(Json("""{"ok":true,"supportsListing":true,"models":["model-from-a"],"message":"Connected — 1 model."}"""));

        // Wait for a POSITIVE signal that A's reply was processed before asserting the picker is
        // absent. Asserting an absence directly passes on the first evaluation — before the
        // continuation has even run — which would make this green against code with no fence.
        provider.WaitForAssertion(() =>
            Assert.IsFalse(provider.Find(".chk-ai-validate-btn").TextContent.Contains("Validating"),
                "validation finished"));

        Assert.AreEqual(0, provider.FindAll(".chk-ai-model-select").Count,
            "a reply about endpoint A must not install a model picker for endpoint B");
        Assert.IsFalse(provider.Markup.Contains("model-from-a"),
            "…and must not leak A's model ids into the form");
    }

    /// <summary>Same fence on the dry run: a verdict describes the config it was issued for, so
    /// any edit while it is in flight makes it stale. Showing it anyway would tell the operator
    /// their current config is up when it was never contacted.</summary>
    [TestMethod]
    public async Task ASlowTestVerdict_IsDiscardedWhenTheConfigChangedMeanwhile()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, _) = Ctx(r => r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/test"
            ? release.Task
            : Task.FromResult(Default(r)));
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.Id = 4;
        vm.ProviderConfig["baseUrl"] = "https://a.example/v1";
        vm.ProviderConfig["model"] = "m";
        vm.ProviderConfig["prompt"] = "ping";
        vm.ProviderConfig["expectContains"] = "pong";

        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => provider.Find(".chk-test-btn"));
        provider.Find(".chk-test-btn").Click();

        Type(provider, "chk-cfg-baseUrl", "https://b.example/v1");
        Assert.AreEqual("https://b.example/v1", vm.ProviderConfig["baseUrl"],
            "sanity: the edit must actually have landed, or this test proves nothing");
        release.SetResult(Json("""{"outcome":"up","ok":true,"latencyMs":10,"metrics":[]}"""));

        // Wait for a POSITIVE signal that the reply was processed — the button leaving its busy
        // label — before asserting the verdict is absent. Asserting an absence directly would
        // pass on the first evaluation, before the response had even been handled, which makes
        // the test green against code that has no fence at all.
        provider.WaitForAssertion(() =>
            Assert.IsFalse(provider.Find(".chk-test-btn").TextContent.Contains("Testing"),
                "the dry run finished"));

        Assert.AreEqual(0, provider.FindAll(".chk-test-verdict").Count,
            "a verdict for the previous config must not be shown against the edited one");
    }

    /// <summary>
    /// Validate and Test must not share a generation counter. When they did, starting Test
    /// mid-Validate invalidated Validate's token, so Validate's <c>finally</c> refused to clear
    /// its busy flag and the button stayed "Validating…" and disabled until the dialog was reset.
    /// </summary>
    [TestMethod]
    public async Task OverlappingValidateAndTest_DoNotStrandEachOthersBusyState()
    {
        var releaseValidate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, _) = Ctx(r => r.RequestUri!.AbsolutePath switch
        {
            "/statuscheck/providers/ai/models" => releaseValidate.Task,
            "/statuscheck/providers/ai/test" => Task.FromResult(Json("""{"outcome":"up","ok":true,"latencyMs":5,"metrics":[]}""")),
            _ => Task.FromResult(Default(r)),
        });
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.Id = 6;
        vm.ProviderConfig["baseUrl"] = "https://ai.example/v1";
        vm.ProviderConfig["model"] = "m";
        vm.ProviderConfig["prompt"] = "ping";
        vm.ProviderConfig["expectContains"] = "pong";

        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => provider.Find(".chk-ai-validate-btn"));

        provider.Find(".chk-ai-validate-btn").Click();      // in flight
        provider.Find(".chk-test-btn").Click();             // completes while Validate is pending
        provider.WaitForAssertion(() => provider.Find(".chk-test-verdict"));

        releaseValidate.SetResult(Json("""{"ok":true,"supportsListing":true,"models":["m"],"message":"Connected — 1 model."}"""));

        provider.WaitForAssertion(() =>
        {
            var validateBtn = provider.Find(".chk-ai-validate-btn");
            Assert.IsFalse(validateBtn.HasAttribute("disabled"),
                "Validate must not be left permanently disabled by an unrelated Test");
            Assert.IsFalse(validateBtn.TextContent.Contains("Validating"),
                "…nor stranded showing its busy label");
        });
    }

    /// <summary>Testing an incomplete config can only come back as a schema 422, so the button
    /// says so by being disabled rather than spending a round trip to report what the form
    /// already knows.</summary>
    [TestMethod]
    public async Task TestConnection_IsDisabledUntilTheRequiredFieldsAreFilled()
    {
        var (ctx, requests) = Ctx();
        using var _ctx = ctx;
        var provider = await Open(ctx, NewAiCheck());     // brand-new, everything blank

        provider.WaitForAssertion(() =>
            Assert.IsTrue(provider.Find(".chk-test-btn").HasAttribute("disabled"),
                "a blank AI check cannot be usefully tested"));

        provider.Find(".chk-test-btn").Click();
        Assert.IsFalse(requests.Any(r => r.Path.EndsWith("/test")), "a disabled Test makes no request");
    }

    /// <summary>The counterpart: a saved check whose only blank required field is the SECRET is
    /// complete, because blank means "keep the stored key" server-side. Gating on raw blankness
    /// would make an existing, working check untestable.</summary>
    [TestMethod]
    public async Task TestConnection_IsEnabledForASavedCheckWhoseOnlyBlankFieldIsTheStoredSecret()
    {
        var (ctx, _) = Ctx();
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.Id = 21;                                        // saved ⇒ a stored key may exist
        vm.ProviderConfig["baseUrl"] = "https://ai.example/v1";
        vm.ProviderConfig["model"] = "gpt-4o-mini";
        vm.ProviderConfig["prompt"] = "ping";
        vm.ProviderConfig["expectContains"] = "pong";
        vm.ProviderConfig["apiKey"] = string.Empty;        // blank = keep stored

        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() =>
            Assert.IsFalse(provider.Find(".chk-test-btn").HasAttribute("disabled"),
                "a blank secret on a saved check means 'keep the stored one', not 'incomplete'"));
    }

    // ---- #435 second review: retiring COMPLETED results, and schema-valid gating ----

    /// <summary>
    /// The in-flight fences only reject replies that land late. A verdict already ON SCREEN
    /// describes the config it was obtained for, so editing that config makes it a lie —
    /// "UP" sitting under a form that now points somewhere else.
    /// </summary>
    [TestMethod]
    public async Task EditingTheConfigAfterADryRun_RetiresTheVerdict()
    {
        var (ctx, _) = Ctx(r => Task.FromResult(r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/test"
            ? Json("""{"outcome":"up","ok":true,"latencyMs":12,"metrics":[]}""")
            : Default(r)));
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.Id = 31;
        vm.ProviderConfig["baseUrl"] = "https://a.example/v1";
        vm.ProviderConfig["model"] = "m";
        vm.ProviderConfig["prompt"] = "ping";
        vm.ProviderConfig["expectContains"] = "pong";

        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => provider.Find(".chk-test-btn"));
        provider.Find(".chk-test-btn").Click();
        provider.WaitForAssertion(() => provider.Find(".chk-test-verdict"));   // completed, on screen

        Type(provider, "chk-cfg-baseUrl", "https://b.example/v1");

        provider.WaitForAssertion(() => Assert.AreEqual(0, provider.FindAll(".chk-test-verdict").Count,
            "a completed verdict must not survive an edit to the config it described"));
    }

    /// <summary>Changing the endpoint retires what we learned about the previous one — the
    /// verdict and, critically, the model list, which would otherwise offer endpoint A's ids for
    /// saving against endpoint B. The revealed fields must NOT collapse: an edit hiding values
    /// the operator already typed would be its own bug.</summary>
    [TestMethod]
    public async Task ChangingTheBaseUrlAfterValidating_RetiresTheModelList_ButKeepsTheFieldsRevealed()
    {
        var (ctx, _) = Ctx(r => Task.FromResult(r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? Json("""{"ok":true,"supportsListing":true,"models":["a-only-model"],"message":"Connected — 1 model."}""")
            : Default(r)));
        using var _ctx = ctx;
        var vm = NewAiCheck();
        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://a.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();
        provider.WaitForAssertion(() => provider.Find(".chk-ai-model-select"));

        Type(provider, "chk-cfg-prompt", "ping");          // a value the operator would hate to lose
        Type(provider, "chk-cfg-baseUrl", "https://b.example/v1");

        provider.WaitForAssertion(() =>
        {
            Assert.AreEqual(0, provider.FindAll(".chk-ai-model-select").Count,
                "endpoint A's model list must not be offered for endpoint B");
            Assert.IsFalse(provider.Markup.Contains("a-only-model"));
            provider.Find(".chk-cfg-model");               // back to free text, not hidden
            provider.Find(".chk-cfg-prompt");              // …and the form did not collapse
            Assert.AreEqual("ping", vm.ProviderConfig["prompt"], "typed values survive the edit");
        });
    }

    /// <summary>Presence is not validity. A number field holding "not-a-number" is already
    /// flagged inline, so the dry run can only come back as a 422 — the button must say so by
    /// being disabled rather than spending the round trip.</summary>
    [TestMethod]
    public async Task TestConnection_IsDisabledWhenAFieldIsPresentButSchemaInvalid()
    {
        var (ctx, requests) = Ctx();
        using var _ctx = ctx;
        var vm = NewAiCheck();
        vm.ProviderType = "http";
        vm.Id = 41;
        vm.ProviderConfig["url"] = "https://ok.example/health";
        vm.ProviderConfig["expectedStatusCode"] = "not-a-number";

        var provider = await Open(ctx, vm);

        provider.WaitForAssertion(() =>
            Assert.IsTrue(provider.Find(".chk-test-btn").HasAttribute("disabled"),
                "a present-but-invalid schema value must not enable the dry run"));

        provider.Find(".chk-test-btn").Click();
        Assert.IsFalse(requests.Any(r => r.Path.EndsWith("/test")), "…and makes no request");
    }

    // ---- #435 fourth review: closing the dialog cancels outstanding work ----

    /// <summary>
    /// The generation fences stop a stale reply being RENDERED; they do nothing about the work.
    /// Without a real token, closing the dialog mid-probe left the outbound provider request,
    /// the authenticated API scope serving it, and this component instance alive until the
    /// remote end answered or the backstop fired.
    /// </summary>
    [TestMethod]
    public async Task ClosingTheDialog_CancelsAnOutstandingDryRun()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, handler) = CtxCapturingToken(r => r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/test"
            ? held.Task
            : Task.FromResult(Default(r)));
        using var _ctx = ctx;

        var vm = NewAiCheck();
        vm.Id = 55;
        vm.ProviderConfig["baseUrl"] = "https://ai.example/v1";
        vm.ProviderConfig["model"] = "gpt-4o-mini";
        vm.ProviderConfig["prompt"] = "ping";
        vm.ProviderConfig["expectContains"] = "pong";

        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => provider.Find(".chk-test-btn"));
        provider.Find(".chk-test-btn").Click();                       // in flight, held open

        provider.WaitForAssertion(() =>
            Assert.IsTrue(handler.LastToken.CanBeCanceled,
                "the dry run must be issued with a real cancellation token, not the default"));

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        provider.WaitForAssertion(() =>
            Assert.IsTrue(handler.LastToken.IsCancellationRequested,
                "closing the dialog must cancel the outstanding request, not just ignore its reply"));

        held.SetResult(Json("""{"outcome":"up","ok":true,"latencyMs":1,"metrics":[]}"""));
    }

    /// <summary>Same contract for discovery, which holds an outbound request to an
    /// operator-supplied host and so is the one that most needs to stop.</summary>
    [TestMethod]
    public async Task ClosingTheDialog_CancelsAnOutstandingValidate()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, handler) = CtxCapturingToken(r => r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? held.Task
            : Task.FromResult(Default(r)));
        using var _ctx = ctx;

        var provider = await Open(ctx, NewAiCheck());
        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://ai.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();

        provider.WaitForAssertion(() => Assert.IsTrue(handler.LastToken.CanBeCanceled));

        provider.FindAll(".mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        provider.WaitForAssertion(() =>
            Assert.IsTrue(handler.LastToken.IsCancellationRequested,
                "closing the dialog must cancel an outstanding discovery call"));

        held.SetResult(Json("""{"ok":true,"supportsListing":false,"models":[],"message":"x"}"""));
    }

    /// <summary>Switching provider also makes outstanding work irrelevant — the fence alone
    /// would leave it running against a provider nobody is configuring any more.</summary>
    [TestMethod]
    public async Task SwitchingProvider_CancelsAnOutstandingValidate()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, handler) = CtxCapturingToken(r => r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? held.Task
            : Task.FromResult(Default(r)));
        using var _ctx = ctx;

        var provider = await Open(ctx, NewAiCheck());
        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://ai.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(handler.LastToken.CanBeCanceled));

        var select = provider.FindComponents<MudSelect<string>>()
            .Single(s => s.Instance.Class is not null && s.Instance.Class.Contains("chk-provider-type"));
        await provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("http"));

        provider.WaitForAssertion(() =>
            Assert.IsTrue(handler.LastToken.IsCancellationRequested,
                "switching provider cancels the superseded discovery call"));

        held.SetResult(Json("""{"ok":true,"supportsListing":false,"models":[],"message":"x"}"""));
    }

    // ---- #435 fifth review: a config edit cancels the work it superseded ----

    /// <summary>
    /// Editing the endpoint mid-validate has to CANCEL the call, not merely fence its reply.
    /// Fencing alone left the request running while <c>_validating</c> guarded re-entry, so the
    /// operator could not validate the configuration they had just typed until the abandoned
    /// call returned — up to the full backstop with a provider that ignores cancellation.
    /// </summary>
    [TestMethod]
    public async Task EditingTheBaseUrlMidValidate_CancelsIt_AndFreesTheActionImmediately()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, handler) = CtxCapturingToken(r => r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/models"
            ? held.Task
            : Task.FromResult(Default(r)));
        using var _ctx = ctx;

        var provider = await Open(ctx, NewAiCheck());
        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://a.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(handler.LastToken.CanBeCanceled));

        // The operator retypes the endpoint while A's call is still outstanding.
        Type(provider, "chk-cfg-baseUrl", "https://b.example/v1");

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(handler.LastToken.IsCancellationRequested,
                "editing the endpoint cancels the call it superseded");
            var btn = provider.Find(".chk-ai-validate-btn");
            Assert.IsFalse(btn.HasAttribute("disabled"),
                "…and the action is usable again at once, not after the backstop");
            Assert.IsFalse(btn.TextContent.Contains("Validating"));
        });

        held.SetResult(Json("""{"ok":true,"supportsListing":true,"models":["a-only"],"message":"x"}"""));
    }

    /// <summary>Same for the dry run: any probe-relevant edit retires the verdict, so it must
    /// retire the request too and hand the button straight back.</summary>
    [TestMethod]
    public async Task EditingTheConfigMidDryRun_CancelsIt_AndFreesTheActionImmediately()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ctx, handler) = CtxCapturingToken(r => r.RequestUri!.AbsolutePath == "/statuscheck/providers/ai/test"
            ? held.Task
            : Task.FromResult(Default(r)));
        using var _ctx = ctx;

        var vm = NewAiCheck();
        vm.Id = 61;
        vm.ProviderConfig["baseUrl"] = "https://a.example/v1";
        vm.ProviderConfig["model"] = "m";
        vm.ProviderConfig["prompt"] = "ping";
        vm.ProviderConfig["expectContains"] = "pong";

        var provider = await Open(ctx, vm);
        provider.WaitForAssertion(() => provider.Find(".chk-test-btn"));
        provider.Find(".chk-test-btn").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(handler.LastToken.CanBeCanceled));

        Type(provider, "chk-cfg-prompt", "a different canary prompt");

        provider.WaitForAssertion(() =>
        {
            Assert.IsTrue(handler.LastToken.IsCancellationRequested,
                "editing probe-relevant config cancels the dry run it superseded");
            var btn = provider.Find(".chk-test-btn");
            Assert.IsFalse(btn.HasAttribute("disabled"),
                "…and Test connection is immediately available for the new config");
        });

        held.SetResult(Json("""{"outcome":"up","ok":true,"latencyMs":1,"metrics":[]}"""));
    }

    /// <summary>The point of freeing the action: the operator can immediately run the
    /// REPLACEMENT request, and it goes out with the newly typed configuration.</summary>
    [TestMethod]
    public async Task AfterAnEditCancelsValidation_TheReplacementRequestCanRunAtOnce()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = new List<string>();
        var (ctx, _handler) = CtxCapturingToken(async r =>
        {
            if (r.RequestUri!.AbsolutePath != "/statuscheck/providers/ai/models") return Default(r);
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync();
            bodies.Add(body);
            return bodies.Count == 1
                ? await held.Task                                  // first call hangs
                : Json("""{"ok":true,"supportsListing":true,"models":["b-model"],"message":"Connected — 1 model."}""");
        });
        using var _ctx = ctx;

        var provider = await Open(ctx, NewAiCheck());
        provider.WaitForAssertion(() => provider.Find(".chk-cfg-baseUrl input"));
        Type(provider, "chk-cfg-baseUrl", "https://a.example/v1");
        provider.Find(".chk-ai-validate-btn").Click();
        provider.WaitForAssertion(() => Assert.AreEqual(1, bodies.Count));

        Type(provider, "chk-cfg-baseUrl", "https://b.example/v1");
        provider.WaitForAssertion(() => Assert.IsFalse(provider.Find(".chk-ai-validate-btn").HasAttribute("disabled")));

        provider.Find(".chk-ai-validate-btn").Click();              // the replacement, straight away

        provider.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, bodies.Count, "the replacement request actually went out");
            StringAssert.Contains(bodies[1], "b.example", "…carrying the newly typed endpoint");
            provider.Find(".chk-ai-model-select");
        });

        held.SetResult(Json("""{"ok":true,"supportsListing":true,"models":["a-only"],"message":"x"}"""));
    }
}
