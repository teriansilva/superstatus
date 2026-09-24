using OpenIddict.Abstractions;
using SuperStatus.Data.DatabaseContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #218 — the aspNetCoreAuth OpenIddict client descriptor is built from the
/// current WEBAPP_HTTP / OIDC_WEB_CLIENT_SECRET and reconciled on every boot, so a
/// self-hoster moving from the local trial to a real deployment doesn't end up
/// with a stale redirect base / secret (which would cause invalid_client). These
/// assert the descriptor reflects its inputs.
/// </summary>
[TestClass]
public class OidcClientDescriptorTests
{
    [TestMethod]
    public void Descriptor_DerivesRedirectUris_FromWebAppBaseUrl()
    {
        var d = SuperStatusIdentityDbInitializer.BuildAspNetCoreAuthDescriptor(
            "https://status.example.com", "a-secret");

        CollectionAssert.Contains(d.RedirectUris.ToList(),
            new Uri("https://status.example.com/signin-oidc"));
        CollectionAssert.Contains(d.PostLogoutRedirectUris.ToList(),
            new Uri("https://status.example.com/signout-callback-oidc"));
    }

    [TestMethod]
    public void Descriptor_CarriesClientIdAndSecret()
    {
        var d = SuperStatusIdentityDbInitializer.BuildAspNetCoreAuthDescriptor(
            "http://localhost:8080", "rotated-secret");

        Assert.AreEqual("aspNetCoreAuth", d.ClientId);
        Assert.AreEqual("rotated-secret", d.ClientSecret);
        // Must be set explicitly so the descriptor-based UpdateAsync (reconcile)
        // doesn't fail "client type cannot be null".
        Assert.AreEqual(OpenIddictConstants.ClientTypes.Confidential, d.ClientType);
    }

    [TestMethod]
    public void Descriptor_ChangingBaseUrl_ChangesRedirectUris()
    {
        var trial = SuperStatusIdentityDbInitializer.BuildAspNetCoreAuthDescriptor(
            "http://localhost:8080", "s");
        var prod = SuperStatusIdentityDbInitializer.BuildAspNetCoreAuthDescriptor(
            "https://status.example.com", "s");

        CollectionAssert.AreNotEquivalent(
            trial.RedirectUris.ToList(), prod.RedirectUris.ToList());
    }
}
