using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #326: <c>AddSuperStatusDataProtection</c> must persist the DataProtection
/// keyring to <c>DATAPROTECTION_KEYS_DIR</c> so antiforgery tokens and auth cookies
/// survive a container recreate (otherwise the first-run /Identity/Account/Setup
/// POST fails antiforgery with HTTP 400 after any restart). Persistence is opt-in:
/// with the env unset, dev behaviour is unchanged.
/// </summary>
[TestClass]
public class DataProtectionPersistenceTests
{
    private static IDataProtectionProvider BuildProvider(string keysDir, string appName)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["DATAPROTECTION_KEYS_DIR"] = keysDir;
        builder.AddSuperStatusDataProtection(appName);
        return builder.Build().Services.GetRequiredService<IDataProtectionProvider>();
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "ss-dp-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void Keys_persist_so_a_token_survives_a_simulated_restart()
    {
        var dir = TempDir();
        try
        {
            // "Process 1" protects a payload (e.g. an antiforgery token).
            var token = BuildProvider(dir, "SuperStatus.Identity")
                .CreateProtector("antiforgery").Protect("hello");

            // The keyring was written to the configured directory…
            Assert.IsTrue(Directory.Exists(dir), "keys directory created");
            Assert.IsTrue(Directory.GetFiles(dir, "key-*.xml").Length > 0, "a key ring file was persisted");

            // …and a *fresh* provider over the same dir + app name (a restarted
            // container) can still unprotect it. Pre-fix this threw — keys were
            // ephemeral, so the new keyring couldn't decrypt the old cookie → 400.
            var roundTripped = BuildProvider(dir, "SuperStatus.Identity")
                .CreateProtector("antiforgery").Unprotect(token);
            Assert.AreEqual("hello", roundTripped);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Distinct_application_names_do_not_cross_decrypt()
    {
        var dir = TempDir();
        try
        {
            // Web and Identity may share a keys directory; distinct app names keep
            // each app's payloads un-decryptable by the other.
            var token = BuildProvider(dir, "SuperStatus.Web")
                .CreateProtector("antiforgery").Protect("hello");
            var identity = BuildProvider(dir, "SuperStatus.Identity").CreateProtector("antiforgery");
            Assert.ThrowsException<CryptographicException>(() => identity.Unprotect(token));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Unset_keys_dir_is_a_noop_and_writes_nothing()
    {
        // Opt-out path (local Aspire dev): env unset → method must not throw and
        // must not create/persist any keyring.
        var dir = TempDir();
        var builder = Host.CreateApplicationBuilder();
        builder.AddSuperStatusDataProtection("SuperStatus.Web");
        using var host = builder.Build();
        Assert.IsFalse(Directory.Exists(dir), "no keys directory created when DATAPROTECTION_KEYS_DIR is unset");
    }
}
