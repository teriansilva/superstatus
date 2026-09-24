using System.Net;
using SuperStatus.ServiceDefaults;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #218 — single-host OIDC. The web/api services advertise the public
/// issuer (e.g. http://id.localhost:8081) but must make their own back-channel
/// calls over the internal compose host (http://identity:8080), because the
/// public *.localhost host is pinned to loopback by glibc inside the containers.
/// These tests cover the rewrite (happy path), the no-op guard for the
/// reverse-proxy deployment (public == internal), and that unrelated hosts are
/// left untouched.
/// </summary>
[TestClass]
public class IdpBackchannelRewriteHandlerTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string? LastHost { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastHost = request.Headers.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<CapturingHandler> SendCapturingAsync(string publicBase, string internalBase, string requestUrl)
    {
        var capture = new CapturingHandler();
        var handler = new IdpBackchannelRewriteHandler(publicBase, internalBase, capture);
        using var client = new HttpClient(handler);
        await client.GetAsync(requestUrl);
        return capture;
    }

    private static async Task<string?> SendThroughAsync(string publicBase, string internalBase, string requestUrl)
        => (await SendCapturingAsync(publicBase, internalBase, requestUrl)).LastUri?.ToString();

    [TestMethod]
    public void TryCreate_ReturnsNull_WhenAuthoritiesEqual()
    {
        // Reverse-proxy deployment: public and internal are the same URL, so no
        // rewrite handler should be installed (trailing slash is normalized).
        Assert.IsNull(IdpBackchannelRewriteHandler.TryCreate(
            "https://id.status.example.com", "https://id.status.example.com/"));
    }

    [TestMethod]
    public void TryCreate_ReturnsNull_WhenEitherMissing()
    {
        Assert.IsNull(IdpBackchannelRewriteHandler.TryCreate(null, "http://identity:8080"));
        Assert.IsNull(IdpBackchannelRewriteHandler.TryCreate("http://id.localhost:8081", ""));
    }

    [TestMethod]
    public void TryCreate_ReturnsHandler_WhenAuthoritiesDiffer()
    {
        Assert.IsNotNull(IdpBackchannelRewriteHandler.TryCreate(
            "http://id.localhost:8081", "http://identity:8080"));
    }

    [TestMethod]
    public async Task SendAsync_RewritesPublicHostToInternal_ForTokenEndpoint()
    {
        var rewritten = await SendThroughAsync(
            "http://id.localhost:8081", "http://identity:8080",
            "http://id.localhost:8081/connect/token");
        Assert.AreEqual("http://identity:8080/connect/token", rewritten);
    }

    [TestMethod]
    public async Task SendAsync_PreservesPublicHostHeader_SoOpenIddictEmitsPublicEndpoints()
    {
        // The TCP target is internal, but the Host header must stay public so
        // OpenIddict advertises public endpoints and a public issuer.
        var capture = await SendCapturingAsync(
            "http://id.localhost:8081", "http://identity:8080",
            "http://id.localhost:8081/.well-known/openid-configuration");
        Assert.AreEqual("http://identity:8080/.well-known/openid-configuration", capture.LastUri?.ToString());
        Assert.AreEqual("id.localhost:8081", capture.LastHost);
    }

    [TestMethod]
    public async Task SendAsync_RewritesDiscoveryEndpoint()
    {
        var rewritten = await SendThroughAsync(
            "http://id.localhost:8081", "http://identity:8080",
            "http://id.localhost:8081/.well-known/openid-configuration");
        Assert.AreEqual("http://identity:8080/.well-known/openid-configuration", rewritten);
    }

    [TestMethod]
    public async Task SendAsync_LeavesUnrelatedHostsUntouched()
    {
        var passthrough = await SendThroughAsync(
            "http://id.localhost:8081", "http://identity:8080",
            "http://api:8080/statuscheck/summary");
        Assert.AreEqual("http://api:8080/statuscheck/summary", passthrough);
    }
}
