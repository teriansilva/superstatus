using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Web;
using SuperStatus.Web.Components;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #269 — the public status page's server-rendered SEO/social head: branded
/// from SiteSettings and host-correct from the request, with valid JSON-LD.
/// </summary>
[TestClass]
public class SeoHeadTests
{
    private static BunitTestContext Ctx(string settingsJson)
    {
        var ctx = new BunitTestContext();
        var http = new HttpClient(new Stub(settingsJson)) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new SettingsApiClient(http));
        return ctx;
    }

    private static HttpContext Host(string scheme, string host)
    {
        var c = new DefaultHttpContext();
        c.Request.Scheme = scheme;
        c.Request.Host = new HostString(host);
        return c;
    }

    [TestMethod]
    public void Branded_fromSettings_andHost()
    {
        using var ctx = Ctx("""{"title":"Acme"}""");
        var cut = ctx.RenderComponent<SeoHead>(p => p.AddCascadingValue<HttpContext?>(Host("https", "status.acme.com")));

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("<title>Acme — Service Status</title>"), "branded title");
            Assert.IsTrue(cut.Markup.Contains("status.acme.com"), "canonical/og url uses the request host");
            Assert.IsTrue(cut.Markup.Contains("og:title"), "open graph present");
            Assert.IsTrue(cut.Markup.Contains("application/ld+json"), "JSON-LD present");
            Assert.IsTrue(cut.Markup.Contains("\"@type\":\"WebSite\""), "WebSite structured data");
            Assert.IsTrue(cut.Markup.Contains("https://status.acme.com/"), "absolute canonical URL");
        });
    }

    [TestMethod]
    public void Defaults_toSuperStatus_whenUnbranded()
    {
        using var ctx = Ctx("{}"); // no title
        var cut = ctx.RenderComponent<SeoHead>(p => p.AddCascadingValue<HttpContext?>(Host("https", "status.example.org")));

        cut.WaitForAssertion(() =>
            Assert.IsTrue(cut.Markup.Contains("<title>SuperStatus — Service Status</title>"),
                "falls back to the SuperStatus name when no brand title is set"));
    }

    private sealed class Stub(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
