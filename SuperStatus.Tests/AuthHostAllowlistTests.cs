using SuperStatus.ServiceDefaults;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #358 — the host-normalization + allowlist-matching contract shared by
/// Identity (host gate + same-origin redirect) and Web (dynamic-mode issuer
/// validation). Covers the edge cases Hermes flagged: case-folding, trailing
/// dots, IPv6 brackets, explicit-vs-any port, IDN → punycode, and same-origin
/// rejection of a foreign host.
/// </summary>
[TestClass]
public class AuthHostAllowlistTests
{
    [DataTestMethod]
    [DataRow("Status.Example.COM", "status.example.com")]   // case-folded
    [DataRow("status.example.com.", "status.example.com")]   // trailing dot stripped
    [DataRow("  status.example.com  ", "status.example.com")] // trimmed
    [DataRow("20.106.154.24", "20.106.154.24")]              // ipv4 literal
    [DataRow("localhost", "localhost")]                       // single-label
    [DataRow("[::1]", "::1")]                                  // ipv6 unbracketed
    [DataRow("[2001:DB8::1]", "2001:db8::1")]                 // ipv6 canonical/compressed
    public void NormalizeHost_Canonicalizes(string input, string expected)
        => Assert.AreEqual(expected, AuthHostAllowlist.NormalizeHost(input));

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not a host")]
    [DataRow("bad_underscore.example.com")]
    [DataRow("-leadinghyphen.com")]
    public void NormalizeHost_RejectsJunk(string input)
        => Assert.IsNull(AuthHostAllowlist.NormalizeHost(input));

    [TestMethod]
    public void NormalizeHost_FoldsIdnToPunycode()
        => Assert.AreEqual("xn--bcher-kva.example", AuthHostAllowlist.NormalizeHost("Bücher.example"));

    [DataTestMethod]
    [DataRow("status.example.com", "status.example.com")]          // bare host
    [DataRow("20.106.154.24:8081", "20.106.154.24:8081")]          // host:port
    [DataRow("https://status.example.com:8081/signin-oidc", "status.example.com:8081")] // url stripped
    [DataRow("[::1]:8081", "[::1]:8081")]                           // ipv6 + port, rebracketed
    [DataRow("[2001:db8::1]", "[2001:db8::1]")]                     // ipv6, no port, bracketed
    public void Canonical_ProducesStorageForm(string input, string expected)
        => Assert.AreEqual(expected, AuthHostAllowlist.Canonical(input));

    [DataTestMethod]
    [DataRow("host:0")]        // port too low
    [DataRow("host:70000")]    // port too high
    [DataRow("host:abc")]      // non-numeric port
    [DataRow("[::1")]          // unbalanced bracket
    public void Canonical_RejectsInvalid(string input)
        => Assert.IsNull(AuthHostAllowlist.Canonical(input));

    [TestMethod]
    public void Sanitize_DropsInvalid_Dedupes_AndCaps()
    {
        var input = new List<string> { "a.example.com", "A.EXAMPLE.COM", "junk host", "b.example.com" };
        var result = AuthHostAllowlist.Sanitize(input);
        CollectionAssert.AreEqual(new[] { "a.example.com", "b.example.com" }, result);

        // Cap at MaxEntries.
        var many = Enumerable.Range(0, AuthHostAllowlist.MaxEntries + 5).Select(i => $"h{i}.example.com");
        Assert.AreEqual(AuthHostAllowlist.MaxEntries, AuthHostAllowlist.Sanitize(many).Count);
    }

    [TestMethod]
    public void Allows_PortlessEntry_MatchesAnyPort()
    {
        var list = new[] { "status.example.com" };
        Assert.IsTrue(AuthHostAllowlist.Allows(list, "status.example.com", 8081));
        Assert.IsTrue(AuthHostAllowlist.Allows(list, "status.example.com", null));
        Assert.IsTrue(AuthHostAllowlist.Allows(list, "STATUS.example.com", 443));
    }

    [TestMethod]
    public void Allows_EntryWithPort_MatchesOnlyThatPort()
    {
        var list = new[] { "20.106.154.24:8081" };
        Assert.IsTrue(AuthHostAllowlist.Allows(list, "20.106.154.24", 8081));
        Assert.IsFalse(AuthHostAllowlist.Allows(list, "20.106.154.24", 9090));
        Assert.IsFalse(AuthHostAllowlist.Allows(list, "20.106.154.24", null));
    }

    [TestMethod]
    public void Allows_RejectsForeignHost_AndEmptyList()
    {
        Assert.IsFalse(AuthHostAllowlist.Allows(new[] { "status.example.com" }, "evil.example.net", 8081));
        Assert.IsFalse(AuthHostAllowlist.Allows(Array.Empty<string>(), "status.example.com", 8081));
        Assert.IsFalse(AuthHostAllowlist.Allows(new[] { "status.example.com" }, "not a host", 8081));
    }

    [TestMethod]
    public void Allows_Ipv6_MatchesRegardlessOfBracketsOrCase()
    {
        var list = new[] { "[2001:db8::1]:8081" };
        Assert.IsTrue(AuthHostAllowlist.Allows(list, "2001:DB8::1", 8081));
        Assert.IsTrue(AuthHostAllowlist.Allows(list, "[2001:db8::1]", 8081));
    }

    [TestMethod]
    public void IsSameOriginCallback_AcceptsSameSchemeHostPortPath()
    {
        Assert.IsTrue(AuthHostAllowlist.IsSameOriginCallback(
            "http://20.106.154.24:8080/signin-oidc", "20.106.154.24", 8080, "/signin-oidc", "http"));
        // host case-folds; scheme matches (case-insensitively).
        Assert.IsTrue(AuthHostAllowlist.IsSameOriginCallback(
            "https://Status.Example.com:8080/signin-oidc", "status.example.com", 8080, "/signin-oidc", "HTTPS"));
    }

    [DataTestMethod]
    [DataRow("http://evil.example.net:8080/signin-oidc")]   // foreign host
    [DataRow("http://status.example.com:9999/signin-oidc")] // wrong port
    [DataRow("http://status.example.com:8080/evil")]        // wrong path
    [DataRow("https://status.example.com:8080/signin-oidc")]// wrong scheme (same host/port/path)
    [DataRow("/signin-oidc")]                                // relative
    [DataRow(null)]                                          // missing
    public void IsSameOriginCallback_RejectsMismatch(string? callback)
        => Assert.IsFalse(AuthHostAllowlist.IsSameOriginCallback(callback, "status.example.com", 8080, "/signin-oidc", "http"));

    [TestMethod]
    public void TryNormalizeForWrite_AcceptsValid_IgnoresBlanks_Dedupes()
    {
        var ok = AuthHostAllowlist.TryNormalizeForWrite(
            new[] { "a.example.com", "  ", "A.EXAMPLE.COM", "20.106.154.24:8081" }, out var norm, out var err);
        Assert.IsTrue(ok);
        Assert.IsNull(err);
        CollectionAssert.AreEqual(new[] { "a.example.com", "20.106.154.24:8081" }, norm);
    }

    [TestMethod]
    public void TryNormalizeForWrite_EmptyOrAllBlank_IsIntentionalClear()
    {
        Assert.IsTrue(AuthHostAllowlist.TryNormalizeForWrite(new[] { "", "  " }, out var norm, out var err));
        Assert.AreEqual(0, norm.Count);
        Assert.IsNull(err);
        Assert.IsTrue(AuthHostAllowlist.TryNormalizeForWrite(Array.Empty<string>(), out var norm2, out _));
        Assert.AreEqual(0, norm2.Count);
    }

    [TestMethod]
    public void TryNormalizeForWrite_RejectsNonblankInvalid_AndOverLimit()
    {
        // A single bad entry rejects the whole write (the row is left unchanged by the caller).
        Assert.IsFalse(AuthHostAllowlist.TryNormalizeForWrite(
            new[] { "good.example.com", "bad host" }, out var norm, out var err));
        Assert.AreEqual(0, norm.Count);
        Assert.IsNotNull(err);

        var tooMany = Enumerable.Range(0, AuthHostAllowlist.MaxEntries + 1).Select(i => $"h{i}.example.com");
        Assert.IsFalse(AuthHostAllowlist.TryNormalizeForWrite(tooMany, out _, out var err2));
        Assert.IsNotNull(err2);
    }
}
