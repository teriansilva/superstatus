using System.Net;
using System.Text;
using SuperStatus.Identity.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #358 — the CachedAuthHostPolicy caching/hardening contract. The critical
/// guarantee (Hermes r1/r2): once a non-empty allowlist has loaded, a transient
/// API read failure must NOT widen the install back to relaxed; only a successful
/// read that returns empty (the operator cleared it) does. TTL is set to zero so
/// every call re-reads through the stub.
/// </summary>
[TestClass]
public class AuthHostPolicyTests
{
    [TestMethod]
    public async Task RetainsLastKnownGood_OnReadFailure_ButHonorsSuccessfulClear()
    {
        var handler = new StubHandler { Respond = () => JsonHosts("[\"status.example.com\"]") };
        var policy = new CachedAuthHostPolicy(new StubFactory(handler), TimeSpan.Zero);

        // 1. First read succeeds → hardened.
        var loaded = await policy.GetAllowlistAsync();
        CollectionAssert.AreEqual(new[] { "status.example.com" }, loaded.ToArray());

        // 2. API goes down → last-known-good retained (STAYS hardened).
        handler.Respond = () => throw new HttpRequestException("api down");
        var duringOutage = await policy.GetAllowlistAsync();
        CollectionAssert.AreEqual(new[] { "status.example.com" }, duringOutage.ToArray());

        // 3. API recovers and returns EMPTY (operator cleared it) → relaxed.
        handler.Respond = () => JsonHosts("[]");
        var afterClear = await policy.GetAllowlistAsync();
        Assert.AreEqual(0, afterClear.Count);
    }

    [TestMethod]
    public async Task ColdStartWithApiDown_IsRelaxed()
    {
        // Never loaded + API unreachable ⇒ empty ⇒ relaxed (identical to true first run).
        var handler = new StubHandler { Respond = () => throw new HttpRequestException("api down") };
        var policy = new CachedAuthHostPolicy(new StubFactory(handler), TimeSpan.Zero);

        var list = await policy.GetAllowlistAsync();
        Assert.AreEqual(0, list.Count);
    }

    private static HttpResponseMessage JsonHosts(string jsonArray) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"allowedAuthHosts\":{jsonArray}}}", Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Respond());
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api:8080") };
    }
}
