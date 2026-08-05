using SuperStatus.ServiceDefaults;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #218 — OIDC authority contract. Covers the browser-scheme gate (which
/// drives the plain-HTTP cookie relaxation) and the fail-fast guard against a
/// loopback public issuer paired with an HTTPS back-channel authority (the "set
/// IDP_HTTP to a real domain but left IDP_PUBLIC_HTTP at the localhost default"
/// misconfiguration Hermes flagged).
/// </summary>
[TestClass]
public class IdpAuthorityTests
{
    [TestMethod]
    public void BrowserUsesPlainHttp_TrueForHttp_FalseForHttps()
    {
        Assert.IsTrue(IdpAuthority.BrowserUsesPlainHttp("http://id.localhost:8081"));
        Assert.IsFalse(IdpAuthority.BrowserUsesPlainHttp("https://id.example.com"));
        Assert.IsFalse(IdpAuthority.BrowserUsesPlainHttp(null));
        Assert.IsFalse(IdpAuthority.BrowserUsesPlainHttp(""));
    }

    [TestMethod]
    public void Validate_Throws_OnLoopbackPublicWithHttpsInternal()
    {
        // The footgun: real HTTPS back-channel, but the public issuer is still the
        // localhost default.
        Assert.ThrowsException<InvalidOperationException>(() =>
            IdpAuthority.Validate("http://id.localhost:8081", "https://id.example.com"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            IdpAuthority.Validate("http://127.0.0.1:8081", "https://id.example.com"));
    }

    [TestMethod]
    public void Validate_DoesNotThrow_ForTheValidShapes()
    {
        // 1. localhost trial: plain-HTTP localhost public + compose-name internal.
        IdpAuthority.Validate("http://id.localhost:8081", "http://identity:8080");
        // 2. reverse-proxy: both the same public HTTPS URL.
        IdpAuthority.Validate("https://id.example.com", "https://id.example.com");
        // 3. TLS proxy in front of the compose stack: public HTTPS, internal name.
        IdpAuthority.Validate("https://id.example.com", "http://identity:8080");
        // 4. Aspire dev path: only IDP_HTTP is set, so public falls back to it —
        //    both an equal https://localhost:<port> URL. Equal ⇒ never the footgun.
        IdpAuthority.Validate("https://localhost:7443", "https://localhost:7443");
    }

    [TestMethod]
    public void Validate_NoOp_WhenEitherUnset()
    {
        IdpAuthority.Validate(null, "https://id.example.com");
        IdpAuthority.Validate("http://id.localhost:8081", null);
        IdpAuthority.Validate("", "");
    }

    [TestMethod]
    public void IsDynamicSelfHost_OnlyWhenBothUnset()
    {
        // #358: the two-port self-host stack — both unset.
        Assert.IsTrue(IdpAuthority.IsDynamicSelfHost(null, null));
        Assert.IsTrue(IdpAuthority.IsDynamicSelfHost("", "  "));

        // Pinned (IDP_PUBLIC_HTTP set) — not dynamic.
        Assert.IsFalse(IdpAuthority.IsDynamicSelfHost("https://id.example.com", null));
        // Reverse-proxy (WEBAPP_HTTP set, issuer from forwarded host) — NOT the
        // two-port dynamic stack; the same-origin/host-gate handlers must stay off.
        Assert.IsFalse(IdpAuthority.IsDynamicSelfHost(null, "https://status.example.com"));
        Assert.IsFalse(IdpAuthority.IsDynamicSelfHost("https://id.example.com", "https://status.example.com"));
    }
}
