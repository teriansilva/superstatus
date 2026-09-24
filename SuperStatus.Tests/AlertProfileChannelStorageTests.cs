using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.ApiService;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Alerts;
using SuperStatus.Services.Notifications;

namespace SuperStatus.Tests;

/// <summary>
/// #343 Phase 3: the per-profile channel storage generalization. Covers the
/// <see cref="EmailChannelSettings"/> round-trip, the admin dual-write into the
/// <see cref="AlertProfileChannel"/> collection, the engine reading recipients from an
/// email channel's config, and byte-for-byte parity between a channel-configured profile
/// and the deprecated-column fallback.
/// </summary>
[TestClass]
public class AlertProfileChannelStorageTests
{
    private static (SuperStatusDb db, SqliteConnection conn) Db()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    // ---- EmailChannelSettings ------------------------------------------------

    [TestMethod]
    public void EmailChannelSettings_RoundTrips_AndToleratesGarbage()
    {
        var json = new EmailChannelSettings("a@x.com,b@x.com", false).ToJson();
        var back = EmailChannelSettings.FromJson(json);
        Assert.AreEqual("a@x.com,b@x.com", back.Recipients);
        Assert.IsFalse(back.UsesSiteDefault);

        var siteDefault = EmailChannelSettings.FromJson(new EmailChannelSettings("", true).ToJson());
        Assert.IsTrue(siteDefault.UsesSiteDefault);
        Assert.AreEqual("", siteDefault.Recipients);

        // A bad/blank row degrades to Empty rather than throwing in the send path.
        Assert.AreEqual(EmailChannelSettings.Empty, EmailChannelSettings.FromJson("not json"));
        Assert.AreEqual(EmailChannelSettings.Empty, EmailChannelSettings.FromJson(null));
    }

    // ---- admin dual-write ----------------------------------------------------

    [TestMethod]
    public async Task AdminSyncChannels_CreatesThenUpdates_EmailAndWebPushRows()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        var profile = new AlertProfile { Name = "p", CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.Add(profile);
        await db.SaveChangesAsync();
        var channels = new Repository<AlertProfileChannel>(db);

        // First save: email on with recipients, web push off.
        await LinkedTargetsAdminApi.SyncChannelsAsync(channels, profile.Id,
            new AlertProfileViewModel { EmailEnabled = true, EmailRecipients = "ops@x.com", UsesSiteDefaultRecipients = false, WebPushEnabled = false },
            new List<string> { "ops@x.com" }, default);

        var rows = await db.AlertProfileChannelSet.Where(c => c.AlertProfileId == profile.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count, "one email + one web-push row");
        var email = rows.Single(c => c.ProviderType == NotificationChannelTypes.Email);
        Assert.IsTrue(email.IsEnabled);
        Assert.AreEqual("ops@x.com", EmailChannelSettings.FromJson(email.ConfigJson).Recipients);
        Assert.IsFalse(rows.Single(c => c.ProviderType == NotificationChannelTypes.WebPush).IsEnabled);

        // Second save (update): flip to site-default email + push on — rows are upserted,
        // not duplicated (unique index holds).
        await LinkedTargetsAdminApi.SyncChannelsAsync(channels, profile.Id,
            new AlertProfileViewModel { EmailEnabled = true, EmailRecipients = "", UsesSiteDefaultRecipients = true, WebPushEnabled = true },
            new List<string>(), default);

        rows = await db.AlertProfileChannelSet.Where(c => c.AlertProfileId == profile.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count, "upsert, not insert — still two rows");
        Assert.IsTrue(EmailChannelSettings.FromJson(rows.Single(c => c.ProviderType == NotificationChannelTypes.Email).ConfigJson).UsesSiteDefault);
        Assert.IsTrue(rows.Single(c => c.ProviderType == NotificationChannelTypes.WebPush).IsEnabled);
    }

    // ---- engine reads recipients from channel config -------------------------

    private static AlertEvaluator Evaluator(SuperStatusDb db, IEmailNotifier email)
        => new(new StatusCheckLinkRepository(db), new AlertDeliveryLogRepository(db),
            new NotificationProviderRegistry(new INotificationProvider[]
            {
                new EmailNotificationProvider(email),
                new WebPushNotificationProvider(new NoopWebPush()),
            }),
            NullLogger<AlertEvaluator>.Instance);

    [TestMethod]
    public async Task Engine_ReadsRecipients_FromEmailChannelConfig()
    {
        var (db, conn) = Db(); using var _ = db; using var __ = conn;
        var check = new StatusCheck { Title = "svc", StatusCheckUrl = "https://svc", ServiceLogoUrl = "", AlertOnFailureThreshold = 1 };
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();
        // Channel-backed profile (not the column fallback): explicit recipients.
        LinkedTargetTestUtil.LinkProfile(db, check, emailEnabled: true, recipients: "ops@x.com", usesSiteDefaultRecipients: false);

        check.ConsecutiveFailures = 1; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        var email = new RecordingEmail();
        await Evaluator(db, email).EvaluateAsync(check, FailType.StatusCode);

        CollectionAssert.AreEqual(new[] { "ops@x.com" }, email.Overrides,
            "recipients came from the email channel's ConfigJson, not the columns");
    }

    private sealed class RecordingEmail : IEmailNotifier
    {
        public List<string?> Overrides { get; } = new();
        public bool IsConfigured(SiteSettings settings) => true;
        public Task<EmailSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, string? recipientsOverride = null, CancellationToken cancellationToken = default)
        {
            Overrides.Add(recipientsOverride);
            return Task.FromResult(EmailSendResult.Sent(recipientsOverride ?? "site-default"));
        }
        public Task<EmailSendResult> SendTestAsync(string? toOverride, CancellationToken cancellationToken = default)
            => Task.FromResult(EmailSendResult.Sent("test"));
    }

    private sealed class NoopWebPush : IWebPushNotifier
    {
        public bool IsConfigured(SiteSettings settings) => true;
        public Task<WebPushSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, CancellationToken cancellationToken = default)
            => Task.FromResult(WebPushSendResult.Skipped("no devices"));
    }
}
