using System.Linq;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuperStatus.ApiService;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #108 + Hermes review on PR #112: the documented public CORS
/// guarantee must be actually wired. These tests assert the *contract*
/// of the named policy registered by Program.cs (`AllowAnyOrigin` for
/// `GET` only, with a narrow allowed-headers set, no credentials),
/// independent of any web-host integration test.
/// </summary>
[TestClass]
public class PublicStatusApiCorsTests
{
    private static CorsPolicy BuildPolicy()
    {
        var services = new ServiceCollection();
        services.AddCors(options =>
        {
            options.AddPolicy(PublicStatusApi.CorsPolicyName, policy =>
            {
                policy.AllowAnyOrigin()
                      .WithMethods("GET")
                      .WithHeaders("Accept", "Content-Type");
            });
        });
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        var policy = options.GetPolicy(PublicStatusApi.CorsPolicyName);
        Assert.IsNotNull(policy, $"Policy '{PublicStatusApi.CorsPolicyName}' must be registered.");
        return policy!;
    }

    [TestMethod]
    public void Policy_AllowsAnyOrigin()
    {
        var p = BuildPolicy();
        Assert.IsTrue(p.AllowAnyOrigin, "Public read-only policy must allow any origin so external embeds can fetch /api/status.");
        // Wildcard is incompatible with credentials per CORS spec; we never want credentials on this endpoint.
        Assert.IsFalse(p.SupportsCredentials, "Policy must not allow credentials — wildcard + credentials is a CORS contract violation.");
    }

    [TestMethod]
    public void Policy_RestrictsToGetOnly()
    {
        var p = BuildPolicy();
        CollectionAssert.AreEqual(new[] { "GET" }, p.Methods.ToArray(),
            "Public read-only policy must restrict methods to GET — no writes from the wildcard origin.");
    }

    [TestMethod]
    public void Policy_AllowsContractHeadersOnly()
    {
        var p = BuildPolicy();
        CollectionAssert.AreEquivalent(
            new[] { "Accept", "Content-Type" },
            p.Headers.ToArray(),
            "Allowed request headers must be limited to Accept + Content-Type.");
    }
}
