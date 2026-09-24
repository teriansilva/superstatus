using System.Net;
using System.Net.Http.Json;
using System.Text;
using SuperStatus.Web;

namespace SuperStatus.Tests;

/// <summary>
/// #433 — the two probe-related <see cref="StatusApiClient"/> calls must absorb a failed call
/// rather than throw it.
///
/// The dialog invokes these from Blazor event handlers whose only protection is a
/// <c>finally</c>, so anything that escapes faults the circuit and takes the whole dialog down
/// — instead of showing "the test could not be run". A refused connection, a timeout, a body
/// that disconnects mid-read and malformed 2xx JSON all have to land as the documented neutral
/// result.
/// </summary>
[TestClass]
public class StatusApiClientProbeCallsTests
{
    private sealed class ThrowingHandler(Func<Exception> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw factory();
    }

    private sealed class FixedHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response);
    }

    private static StatusApiClient Client(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://api.test") });

    private static readonly Dictionary<string, string> AnyConfig = new() { ["url"] = "https://x.example" };

    // ---- transport failures --------------------------------------------------

    [TestMethod]
    public async Task TestCheckProvider_AConnectionFailure_ReturnsNull_RatherThanThrowing()
    {
        var client = Client(new ThrowingHandler(() => new HttpRequestException("Connection refused")));

        var result = await client.TestCheckProviderAsync("http", 0, AnyConfig);

        Assert.IsNull(result, "a failed call is reported as 'could not run', not thrown into a Blazor event");
    }

    [TestMethod]
    public async Task TestCheckProvider_ATimeout_ReturnsNull()
    {
        var client = Client(new ThrowingHandler(() => new TaskCanceledException("The request timed out")));

        Assert.IsNull(await client.TestCheckProviderAsync("http", 0, AnyConfig));
    }

    [TestMethod]
    public async Task DiscoverAiModels_AConnectionFailure_ReturnsACalmFailure()
    {
        var client = Client(new ThrowingHandler(() => new HttpRequestException("Connection refused")));

        var result = await client.DiscoverAiModelsAsync(0, "https://ai.example/v1", null);

        Assert.IsNotNull(result);
        Assert.IsFalse(result!.Ok);
        StringAssert.Contains(result.Message!, "Could not reach the endpoint");
    }

    // ---- malformed success bodies -------------------------------------------

    [TestMethod]
    public async Task TestCheckProvider_AMalformed2xxBody_ReturnsNull()
    {
        var bad = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ this is not json", Encoding.UTF8, "application/json"),
        };
        var client = Client(new FixedHandler(bad));

        Assert.IsNull(await client.TestCheckProviderAsync("http", 0, AnyConfig),
            "a 200 with an unparseable body must not escape as a JsonException");
    }

    [TestMethod]
    public async Task DiscoverAiModels_AMalformed2xxBody_ReturnsACalmFailure()
    {
        var bad = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "application/json"),
        };
        var client = Client(new FixedHandler(bad));

        var result = await client.DiscoverAiModelsAsync(0, "https://ai.example/v1", null);

        Assert.IsNotNull(result);
        Assert.IsFalse(result!.Ok);
    }

    // ---- the deliberate non-absorption --------------------------------------

    /// <summary>Caller cancellation is the dialog going away, not the call failing. Swallowing
    /// it would install a bogus verdict for a request nobody is waiting on any more, so it is
    /// deliberately allowed to propagate.</summary>
    [TestMethod]
    public async Task AGenuineCallerCancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = Client(new ThrowingHandler(() => new OperationCanceledException()));

        // Asserted on the base type, not an exact match: HttpClient surfaces a cancelled token
        // as TaskCanceledException, which derives from OperationCanceledException. What matters
        // is that it PROPAGATED rather than being turned into a verdict.
        Exception? thrown = null;
        try { await client.TestCheckProviderAsync("http", 0, AnyConfig, cts.Token); }
        catch (Exception ex) { thrown = ex; }

        Assert.IsInstanceOfType(thrown, typeof(OperationCanceledException),
            $"caller cancellation must propagate, got: {thrown?.GetType().Name ?? "no exception"}");
    }

    // ---- the documented pass-through still works ----------------------------

    [TestMethod]
    public async Task A422_IsSurfacedAsADownVerdictCarryingTheServerReason()
    {
        var rejected = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"message":"AI / LLM endpoint: missing required 'Model'."}""",
                Encoding.UTF8, "application/json"),
        };
        var client = Client(new FixedHandler(rejected));

        var result = await client.TestCheckProviderAsync("ai", 0, AnyConfig);

        Assert.IsNotNull(result);
        Assert.AreEqual("down", result!.Outcome);
        StringAssert.Contains(result.Message!, "missing required");
    }

    // ---- #435 sixth review: cancellation survives the error-body read ----

    /// <summary>Content whose body never finishes streaming until the token is cancelled — the
    /// window between "an error status arrived" and "its body has been read".</summary>
    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.Delay(Timeout.Infinite);

        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    /// <summary>
    /// Cancelling against a non-success response whose body never finishes streaming must
    /// propagate, not collapse into a neutral verdict.
    ///
    /// <para><b>Honest note on what this does and does not prove.</b> It passes on the
    /// pre-fix code too, and that is not a flaw in the test — it is a fact about the call path.
    /// <c>HttpClient.SendAsync</c> defaults to <c>HttpCompletionOption.ResponseContentRead</c>,
    /// so <c>PostAsJsonAsync</c> buffers the entire body before it returns; cancellation during
    /// that read therefore surfaces from the SEND, which the outer catch filter already
    /// rethrows. By the time <c>ReadProblemMessageAsync</c> runs, the content is in memory and
    /// its read cannot block. Verified directly: with this content, <c>PostAsJsonAsync</c>
    /// itself threw <c>TaskCanceledException</c>.</para>
    ///
    /// <para>The helper's blanket catch was still wrong and is now filtered — it would bite
    /// immediately if anyone switched these calls to <c>ResponseHeadersRead</c>. This test pins
    /// the end-to-end contract; it is not the red→green proof of that guard.</para>
    /// </summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task Cancelling_WhileReadingAnErrorBody_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        var rejected = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new BlockingContent() };
        var client = Client(new FixedHandler(rejected));

        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        Exception? thrown = null;
        try { await client.TestCheckProviderAsync("ai", 0, AnyConfig, cts.Token); }
        catch (Exception ex) { thrown = ex; }

        Assert.IsInstanceOfType(thrown, typeof(OperationCanceledException),
            $"cancellation during the error-body read must propagate, got: {thrown?.GetType().Name ?? "no exception"}");
    }

    /// <summary>Same end-to-end contract on the discovery call, which shares the helper. Same
    /// caveat as above about which layer actually raises it today.</summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task DiscoverAiModels_CancellingWhileReadingAnErrorBody_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        var rejected = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new BlockingContent() };
        var client = Client(new FixedHandler(rejected));

        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        Exception? thrown = null;
        try { await client.DiscoverAiModelsAsync(0, "https://ai.example/v1", null, cts.Token); }
        catch (Exception ex) { thrown = ex; }

        Assert.IsInstanceOfType(thrown, typeof(OperationCanceledException),
            $"cancellation during the error-body read must propagate, got: {thrown?.GetType().Name ?? "no exception"}");
    }

    /// <summary>The guard is specifically about CALLER cancellation: an error body that is
    /// merely unreadable still collapses to the documented neutral result rather than throwing.</summary>
    [TestMethod]
    public async Task AnUnreadableErrorBody_WithoutCancellation_StillReturnsTheNeutralResult()
    {
        var rejected = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("<html>not json at all</html>", Encoding.UTF8, "application/json"),
        };
        var client = Client(new FixedHandler(rejected));

        var result = await client.TestCheckProviderAsync("ai", 0, AnyConfig);

        Assert.IsNull(result, "an unparseable error body is 'no message', not an exception");
    }
}
