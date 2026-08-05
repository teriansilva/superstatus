using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Services.Updates;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #249 (epic #248): the GitHub Releases update check — verdicts for
/// newer / equal / older / edge-build / non-200 / malformed, error-tolerant.
/// </summary>
[TestClass]
public class UpdateCheckServiceTests
{
    private sealed class StubVersion(string version, string channel) : IAppVersionProvider
    {
        public AppVersionInfo Current { get; } = new(version, channel);
    }

    private sealed class CannedHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }

    private static GitHubUpdateCheckService Build(IAppVersionProvider version, HttpMessageHandler handler)
        => new(new SingleClientFactory(handler), version, NullLogger<GitHubUpdateCheckService>.Instance);

    private static string Release(string tag) =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "https://github.com/teriansilva/superstatus/releases/tag/{{tag}}" }""";

    [TestMethod]
    public async Task NewerRelease_isUpdateAvailable_withNotesUrl()
    {
        var svc = Build(new StubVersion("1.0.0", "latest"), new CannedHandler(HttpStatusCode.OK, Release("v1.2.0")));
        var r = await svc.CheckAsync();
        Assert.AreEqual(UpdateStatus.UpdateAvailable, r.Status);
        Assert.AreEqual("1.2.0", r.LatestVersion);
        Assert.AreEqual("https://github.com/teriansilva/superstatus/releases/tag/v1.2.0", r.ReleaseNotesUrl);
        Assert.IsNull(r.Error);
    }

    [TestMethod]
    public async Task SameVersion_isUpToDate()
    {
        var svc = Build(new StubVersion("1.2.0", "latest"), new CannedHandler(HttpStatusCode.OK, Release("v1.2.0")));
        var r = await svc.CheckAsync();
        Assert.AreEqual(UpdateStatus.UpToDate, r.Status);
    }

    [TestMethod]
    public async Task RunningAheadOfLatest_isUpToDate()
    {
        var svc = Build(new StubVersion("1.3.0", "latest"), new CannedHandler(HttpStatusCode.OK, Release("v1.2.0")));
        var r = await svc.CheckAsync();
        Assert.AreEqual(UpdateStatus.UpToDate, r.Status);
    }

    [TestMethod]
    public async Task EdgeBuild_skipsTheCallAndIsUpToDate()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, Release("v9.9.9"));
        var svc = Build(new StubVersion("0.0.0-dev", "edge"), handler);
        var r = await svc.CheckAsync();
        Assert.AreEqual(UpdateStatus.UpToDate, r.Status);
        Assert.AreEqual(0, handler.Calls, "an edge build must not hit the network");
    }

    [TestMethod]
    public async Task RateLimited_isUnknown_withError()
    {
        var svc = Build(new StubVersion("1.0.0", "latest"), new CannedHandler(HttpStatusCode.Forbidden, "rate limited"));
        var r = await svc.CheckAsync();
        Assert.AreEqual(UpdateStatus.Unknown, r.Status);
        Assert.IsNotNull(r.Error);
        Assert.IsNull(r.LatestVersion);
    }

    [TestMethod]
    public async Task MalformedTag_isUnknown()
    {
        var svc = Build(new StubVersion("1.0.0", "latest"), new CannedHandler(HttpStatusCode.OK, Release("not-a-version")));
        var r = await svc.CheckAsync();
        Assert.AreEqual(UpdateStatus.Unknown, r.Status);
        Assert.IsNotNull(r.Error);
    }
}
