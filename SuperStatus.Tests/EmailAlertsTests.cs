using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Alerts;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #241 Phase B — SMTP settings (write-only password, stale-on-edit
/// verification) + the email notifier's parse/guard behaviour.
/// </summary>
[TestClass]
public class EmailAlertsTests
{
    private static (SiteSettingsService svc, SiteSettingsRepository repo, SuperStatusDb db, SqliteConnection conn) Build()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var repo = new SiteSettingsRepository(db);
        return (new SiteSettingsService(repo), repo, db, conn);
    }

    private static SiteSettingsViewModel Smtp(string host = "mail.test", string from = "alerts@test", string user = "u",
        string? password = null, string defaults = "ops@test")
        => new()
        {
            SmtpHost = host, SmtpPort = 587, SmtpUseStartTls = true, SmtpUsername = user,
            SmtpPassword = password, SmtpFromAddress = from, SmtpFromName = "SS",
            AlertDefaultRecipients = defaults,
        };

    [TestMethod]
    public async Task Smtp_RoundTrips_MaskedPassword_BlankPreservesStored()
    {
        var (svc, repo, db, conn) = Build(); using var _ = db; using var __ = conn;

        var saved = await svc.SaveSmtpSettingsAsync(Smtp(password: "secret"));
        Assert.IsNull(saved.SmtpPassword, "password never echoed");
        Assert.IsTrue(saved.SmtpPasswordSet);
        Assert.AreEqual("mail.test", saved.SmtpHost);
        Assert.AreEqual("secret", (await repo.GetSingletonAsync())!.SmtpPassword, "stored on the raw row");

        // Re-save with a blank password but the SAME transport → stored password kept.
        var again = await svc.SaveSmtpSettingsAsync(Smtp(password: null));
        Assert.IsTrue(again.SmtpPasswordSet);
        Assert.AreEqual("secret", (await repo.GetSingletonAsync())!.SmtpPassword, "blank preserves the stored password");
    }

    [TestMethod]
    public async Task Smtp_TransportEdit_ClearsVerification_NonTransportEditKeepsIt()
    {
        var (svc, repo, db, conn) = Build(); using var _ = db; using var __ = conn;
        await svc.SaveSmtpSettingsAsync(Smtp(password: "secret"));

        // Simulate a successful "send test".
        var row = await repo.GetSingletonAsync();
        row!.SmtpVerifiedUtc = DateTime.UtcNow;
        await repo.UpdateAndSave(row);

        // A non-transport edit (just the default recipients) keeps the verification.
        await svc.SaveSmtpSettingsAsync(Smtp(password: null, defaults: "newops@test"));
        Assert.IsNotNull((await repo.GetSingletonAsync())!.SmtpVerifiedUtc, "non-transport edit keeps verification");

        // Changing the host (transport) clears it — a stale success can't imply the new relay works.
        await svc.SaveSmtpSettingsAsync(Smtp(host: "mail2.test", password: null, defaults: "newops@test"));
        Assert.IsNull((await repo.GetSingletonAsync())!.SmtpVerifiedUtc, "transport edit clears verification");
    }

    [TestMethod]
    public async Task SendTest_RacingTransportEdit_doesNotVerifyTheNewConfig()
    {
        // Regression (#256): a "send test" started on config A must not stamp
        // SmtpVerifiedUtc onto config B if the operator edits transport mid-flight.
        var (svc, repo, db, conn) = Build(); using var _ = db; using var __ = conn;
        await svc.SaveSmtpSettingsAsync(Smtp(host: "a.test", password: "pA"));

        // Operator edits transport to config B (which clears verification).
        await svc.SaveSmtpSettingsAsync(Smtp(host: "b.test", password: "pB"));

        // The in-flight test (config A) tries to stamp → must NOT (row is B now).
        bool stampedA = await repo.StampSmtpVerifiedIfTransportMatchesAsync(
            "a.test", 587, true, "u", "pA", "alerts@test", DateTime.UtcNow);
        Assert.IsFalse(stampedA, "tested config A no longer matches the row");
        Assert.IsNull(await VerifiedUtc(db), "config B stays unverified");

        // A test that finishes against the CURRENT config (B) does stamp.
        bool stampedB = await repo.StampSmtpVerifiedIfTransportMatchesAsync(
            "b.test", 587, true, "u", "pB", "alerts@test", DateTime.UtcNow);
        Assert.IsTrue(stampedB);
        Assert.IsNotNull(await VerifiedUtc(db));
    }

    // Fresh DB read (ExecuteUpdate bypasses the change tracker).
    private static async Task<DateTime?> VerifiedUtc(SuperStatusDb db)
        => (await db.SiteSettingsSet.AsNoTracking().FirstAsync(x => x.Id == SiteSettings.SingletonId)).SmtpVerifiedUtc;

    [TestMethod]
    public void ParseRecipients_SplitsSeparators_TrimsAndDedups()
    {
        var r = MailKitEmailNotifier.ParseRecipients("a@x.com, b@x.com; a@X.com  c@x.com\nb@x.com");
        CollectionAssert.AreEquivalent(new[] { "a@x.com", "b@x.com", "c@x.com" }, r);
    }

    [TestMethod]
    public async Task Notifier_NotConfigured_ReturnsSkipped_NoThrow()
    {
        var (_, repo, db, conn) = Build(); using var ___ = db; using var ____ = conn;
        var notifier = new MailKitEmailNotifier(repo, NullLogger<MailKitEmailNotifier>.Instance);
        var check = new StatusCheck
        {
            Title = "x", StatusCheckUrl = "u", WebHookOnErrorUrl = "", ServiceLogoUrl = "",
            EmailAlertsEnabled = true, EmailRecipients = "ops@test",
        };

        var result = await notifier.SendAlertAsync(check, AlertTrigger.Outage);
        Assert.AreEqual(EmailSendStatus.Skipped, result.Status, "a guard skip, not a failure");
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("SMTP not configured", result.Detail);
    }

    [TestMethod]
    public async Task BrandingSave_doesNotWipeConfiguredSmtp()
    {
        // Regression (#256): SMTP lives on its own save path, so a normal
        // branding/AI save (which carries no SMTP fields) must not clear the relay.
        var (svc, repo, db, conn) = Build(); using var _ = db; using var __ = conn;
        await svc.SaveSmtpSettingsAsync(Smtp(host: "mail.test", password: "secret"));

        await svc.SaveSettingsAsync(new SiteSettingsViewModel { Title = "Acme", AccentColor = "#112233" });

        var row = await repo.GetSingletonAsync();
        Assert.AreEqual("mail.test", row!.SmtpHost, "SMTP host preserved across a branding save");
        Assert.AreEqual("alerts@test", row.SmtpFromAddress);
        Assert.AreEqual("secret", row.SmtpPassword, "stored SMTP password not orphaned/wiped");
        Assert.AreEqual("Acme", row.Title, "branding still saved");
    }

    [TestMethod]
    public async Task Retarget_WithBlankPassword_ClearsStoredPassword()
    {
        // Retargeting a relay (host change) with no fresh password must drop the old
        // credential so it can't be sent to a different host.
        var (svc, repo, db, conn) = Build(); using var _ = db; using var __ = conn;
        await svc.SaveSmtpSettingsAsync(Smtp(host: "a.test", password: "pA"));

        var saved = await svc.SaveSmtpSettingsAsync(Smtp(host: "b.test", password: null));

        Assert.IsFalse(saved.SmtpPasswordSet, "old credential is not carried to the new relay");
        Assert.AreEqual(string.Empty, (await repo.GetSingletonAsync())!.SmtpPassword);
    }
}
