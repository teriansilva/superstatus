using System.Net;
using System.Text;
using SuperStatus.Services.Providers.Ai;

namespace SuperStatus.Tests;

/// <summary>
/// #433 — AI model discovery (<c>GET {baseUrl}/models</c>).
///
/// This talks to an operator-supplied host, so the tests that matter are the defensive ones:
/// a huge body must not be parsed, junk must degrade to "doesn't list models" rather than an
/// error, and nothing from the upstream response may appear in a returned message.
/// </summary>
[TestClass]
public class AiModelDiscoveryTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AiModelDiscovery Discovery(Func<HttpRequestMessage, HttpResponseMessage> responder, out StubHandler handler)
    {
        handler = new StubHandler(responder);
        return new AiModelDiscovery(new Factory(handler));
    }

    // ---- the happy path ------------------------------------------------------

    [TestMethod]
    public async Task ListsModels_FromTheOpenAiShape_SortedAndDeduplicated()
    {
        var d = Discovery(_ => Json("""
            {"data":[{"id":"gpt-4o-mini"},{"id":"gpt-4.1"},{"id":"gpt-4o-mini"},{"id":"  "},{"id":"llama-3.3"}]}
            """), out var handler);

        var result = await d.ListModelsAsync("https://ai.example/v1", "key");

        Assert.IsTrue(result.Ok);
        Assert.IsTrue(result.SupportsListing);
        CollectionAssert.AreEqual(new[] { "gpt-4.1", "gpt-4o-mini", "llama-3.3" }, result.Models.ToArray(),
            "ids are de-duplicated, blank-stripped and sorted");
        Assert.AreEqual("https://ai.example/v1/models", handler.Last!.RequestUri!.ToString());
        Assert.AreEqual("Bearer", handler.Last.Headers.Authorization!.Scheme);
    }

    [TestMethod]
    public async Task NoApiKey_SendsNoAuthorizationHeader()
    {
        var d = Discovery(_ => Json("""{"data":[{"id":"local-model"}]}"""), out var handler);

        await d.ListModelsAsync("https://local.example/v1", null);

        Assert.IsNull(handler.Last!.Headers.Authorization, "an endpoint that needs no key gets no header");
    }

    // ---- the supported "no listing" shapes -----------------------------------

    [DataTestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.MethodNotAllowed)]
    [DataRow(HttpStatusCode.NotImplemented)]
    public async Task AnEndpointWithoutAModelsRoute_IsReachableButNotListing(HttpStatusCode code)
    {
        var d = Discovery(_ => new HttpResponseMessage(code), out _);

        var result = await d.ListModelsAsync("https://gateway.example/v1", null);

        Assert.IsTrue(result.Ok, "a missing /models route is a supported shape, not a failure");
        Assert.IsFalse(result.SupportsListing);
        Assert.AreEqual(0, result.Models.Count);
    }

    [TestMethod]
    public async Task A200ThatIsNotModelShaped_DegradesToNotListing_RatherThanErroring()
    {
        // An HTML login page, a proxy banner — anything a reverse proxy might answer with.
        var d = Discovery(_ => Json("<html><body>Sign in</body></html>"), out _);

        var result = await d.ListModelsAsync("https://portal.example/v1", null);

        Assert.IsTrue(result.Ok);
        Assert.IsFalse(result.SupportsListing);
    }

    // ---- failures ------------------------------------------------------------

    [DataTestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task ARejectedKey_IsReportedAsSuch(HttpStatusCode code)
    {
        var d = Discovery(_ => new HttpResponseMessage(code), out _);

        var result = await d.ListModelsAsync("https://ai.example/v1", "wrong-key");

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Message!, "rejected the API key");
    }

    [TestMethod]
    public async Task ATransportFailure_NeverLeaksTheUrlOrTheException()
    {
        const string leaky = "No such host (https://user:hunter2@internal.example/v1/models)";
        var d = Discovery(_ => throw new HttpRequestException(leaky), out _);

        var result = await d.ListModelsAsync("https://user:hunter2@internal.example/v1", "key");

        Assert.IsFalse(result.Ok);
        Assert.IsFalse(result.Message!.Contains("hunter2"), "a credential must never reach the client");
        Assert.IsFalse(result.Message.Contains("internal.example"), "the target must never reach the client");
        StringAssert.Contains(result.Message, "Could not reach the endpoint");
    }

    [TestMethod]
    public async Task AnUpstreamErrorBody_IsNeverRelayed()
    {
        var d = Discovery(_ => Json("""{"error":"secret-token-abc123 is invalid for tenant acme-internal"}""",
            HttpStatusCode.InternalServerError), out _);

        var result = await d.ListModelsAsync("https://ai.example/v1", "key");

        Assert.IsFalse(result.Ok);
        Assert.IsFalse(result.Message!.Contains("secret-token-abc123"));
        Assert.IsFalse(result.Message.Contains("acme-internal"));
        StringAssert.Contains(result.Message, "HTTP 500");
    }

    [TestMethod]
    public async Task ABadBaseUrl_IsRejectedWithoutAnyRequest()
    {
        var d = Discovery(_ => Json("{}"), out var handler);

        var result = await d.ListModelsAsync("not a url", null);

        Assert.IsFalse(result.Ok);
        Assert.IsNull(handler.Last, "a malformed base URL must not produce an outbound request");
    }

    [TestMethod]
    public async Task ANonHttpScheme_IsRejected()
    {
        var d = Discovery(_ => Json("{}"), out var handler);

        var result = await d.ListModelsAsync("file:///etc/passwd", null);

        Assert.IsFalse(result.Ok);
        Assert.IsNull(handler.Last, "only http(s) targets are attempted");
    }

    // ---- the bounds ----------------------------------------------------------

    /// <summary>
    /// A cap on the RETURNED list would not stop us parsing a gigantic body first. This proves
    /// the ceiling is enforced on the read: a body far larger than the limit comes back as
    /// "doesn't list models" (its JSON is truncated mid-stream and fails to parse) instead of
    /// being materialised in full.
    /// </summary>
    [TestMethod]
    public async Task AnOversizedBody_IsBoundedByTheReadCeiling()
    {
        int oversize = AiModelDiscovery.MaxResponseBytes * 4;
        var huge = new StringBuilder(oversize + 64).Append("""{"data":[""");
        while (huge.Length < oversize) huge.Append("""{"id":"padpadpadpadpadpadpadpadpadpad"},""");
        huge.Append("""{"id":"tail"}]}""");

        var d = Discovery(_ => Json(huge.ToString()), out _);

        var result = await d.ListModelsAsync("https://hostile.example/v1", null);

        Assert.IsTrue(result.Ok);
        Assert.IsFalse(result.SupportsListing, "a body over the ceiling is truncated and degrades, it is not parsed whole");
    }

    /// <summary>
    /// A cancellation token is only a REQUEST. `HttpClient.Timeout` is cancellation-based too,
    /// so a handler that never observes its token leaves the await pending forever — and because
    /// discovery runs inside an authenticated request, that pins the endpoint's scope (resolved
    /// DbContext and all) indefinitely. The outer `WaitAsync` is what actually abandons it.
    /// </summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task AHandlerThatIgnoresCancellation_IsAbandonedByTheOuterBackstop()
    {
        var neverCompletes = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new IgnoresCancellationHandler(neverCompletes.Task);
        var d = new AiModelDiscovery(new Factory(handler));

        var started = DateTime.UtcNow;
        var result = await d.ListModelsAsync("https://hangs.example/v1", null);
        var elapsed = DateTime.UtcNow - started;

        Assert.IsFalse(result.Ok, "an abandoned discovery reports failure — it does not hang the request");
        StringAssert.Contains(result.Message!, "Could not reach the endpoint");
        Assert.IsTrue(elapsed < AiModelDiscovery.RequestTimeout + TimeSpan.FromSeconds(5),
            $"the backstop must fire near its deadline (took {elapsed.TotalSeconds:0.0}s)");
    }

    /// <summary>Returns a task that never completes and deliberately ignores the token — the
    /// misbehaving-transport case a cooperative timeout alone cannot contain.</summary>
    private sealed class IgnoresCancellationHandler(Task<HttpResponseMessage> never) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => never;
    }

    [TestMethod]
    public void ParseModelIds_DropsAbsurdlyLongIds_AndCapsTheCount()
    {
        string longId = new('x', AiModelDiscovery.MaxModelIdLength + 1);
        var ids = AiModelDiscovery.ParseModelIds($$"""{"data":[{"id":"{{longId}}"},{"id":"ok-model"}]}""");

        CollectionAssert.AreEqual(new[] { "ok-model" }, ids.ToArray(), "an id longer than the cap is dropped, not truncated");

        var many = string.Join(",", Enumerable.Range(0, AiModelDiscovery.MaxModels + 250).Select(i => $$"""{"id":"m{{i}}"}"""));
        Assert.IsTrue(AiModelDiscovery.ParseModelIds($$"""{"data":[{{many}}]}""").Count <= AiModelDiscovery.MaxModels);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not json at all")]
    [DataRow("""{"data":"not an array"}""")]
    [DataRow("""{"unexpected":{"shape":true}}""")]
    [DataRow("""[{"no_id":1}]""")]
    public void ParseModelIds_TreatsAnythingUnparseableAsEmpty(string body)
    {
        Assert.AreEqual(0, AiModelDiscovery.ParseModelIds(body).Count);
    }

    /// <summary>Compatible gateways vary; a bare array and a plain list of strings are both
    /// seen in the wild, and both are cheap to accept.</summary>
    [TestMethod]
    public void ParseModelIds_AcceptsABareArrayAndPlainStrings()
    {
        CollectionAssert.AreEqual(new[] { "a", "b" }, AiModelDiscovery.ParseModelIds("""["b","a"]""").ToArray());
        CollectionAssert.AreEqual(new[] { "c" }, AiModelDiscovery.ParseModelIds("""[{"id":"c"}]""").ToArray());
    }
}
