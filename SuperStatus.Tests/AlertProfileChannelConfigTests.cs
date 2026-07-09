using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.ApiService;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Notifications;

namespace SuperStatus.Tests;

/// <summary>
/// #343 Phase 5: the admin-side channel config round-trip through
/// <see cref="LinkedTargetsAdminApi.PersistSchemaChannelsAsync"/> +
/// <see cref="LinkedTargetsAdminApi.ProjectSchemaChannels"/>: non-secret values round-trip,
/// secret values are masked on read and preserved on blank re-save (the
/// <c>ProviderConfigWriter</c> "leave blank to keep" rule), and schemaless / unknown channel
/// types (email / web push) are skipped by the generic path.
/// </summary>
[TestClass]
public class AlertProfileChannelConfigTests
{
    private sealed class DummyFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static NotificationProviderRegistry Registry()
    {
        var factory = new DummyFactory();
        return new NotificationProviderRegistry(new INotificationProvider[]
        {
            new SlackNotificationProvider(factory, NullLogger<SlackNotificationProvider>.Instance),
            new WebhookNotificationProvider(factory, NullLogger<WebhookNotificationProvider>.Instance),
            new TelegramNotificationProvider(factory, NullLogger<TelegramNotificationProvider>.Instance),
        });
    }

    private static SuperStatusDb NewDb(SqliteConnection conn)
    {
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    // Seed a parent profile (AlertProfileChannel FKs to it) and return its id.
    private static long SeedProfile(SuperStatusDb db)
    {
        var p = new AlertProfile { Name = "p", CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    private static AlertProfileViewModel Body(string providerType, bool enabled, params (string Key, string Value)[] config)
    {
        var vm = new AlertProfileViewModel { Channels = new() };
        var ch = new AlertProfileChannelViewModel { ProviderType = providerType, IsEnabled = enabled };
        foreach (var (k, v) in config) ch.Config[k] = v;
        vm.Channels.Add(ch);
        return vm;
    }

    private static string? StoredUrl(AlertProfileChannel row)
    {
        using var doc = JsonDocument.Parse(row.ConfigJson!);
        return doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
    }

    [TestMethod]
    public async Task Webhook_NonSecretUrl_RoundTrips_Unmasked()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();
        var profileId = SeedProfile(db);

        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Webhook, enabled: true, ("url", "https://hook.example/fire")), registry, default);

        var rows = (await repo.GetMany()).ToList();
        var projected = LinkedTargetsAdminApi.ProjectSchemaChannels(rows, registry);
        var webhook = projected.Single(c => c.ProviderType == NotificationChannelTypes.Webhook);
        Assert.IsTrue(webhook.IsEnabled);
        Assert.AreEqual("https://hook.example/fire", webhook.Config["url"], "non-secret url is echoed on read");
    }

    [TestMethod]
    public async Task Slack_Secret_IsMaskedOnRead_ButStored()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();
        var profileId = SeedProfile(db);

        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Slack, enabled: true, ("url", "https://hooks.slack.com/services/SECRET")), registry, default);

        var rows = (await repo.GetMany()).ToList();
        var stored = rows.Single(r => r.ProviderType == NotificationChannelTypes.Slack);
        Assert.AreEqual("https://hooks.slack.com/services/SECRET", StoredUrl(stored), "the secret is persisted");

        var projected = LinkedTargetsAdminApi.ProjectSchemaChannels(rows, registry);
        var slack = projected.Single(c => c.ProviderType == NotificationChannelTypes.Slack);
        Assert.IsTrue(slack.IsEnabled);
        Assert.IsFalse(slack.Config.ContainsKey("url"), "secret value is never echoed on read");
    }

    [TestMethod]
    public async Task Slack_BlankSecret_PreservesStored_NonBlankReplaces()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();
        var profileId = SeedProfile(db);

        // Initial save with a secret.
        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Slack, true, ("url", "https://hooks.slack.com/services/ONE")), registry, default);

        // Re-save with a BLANK secret (what the masked form posts back untouched) — must keep ONE.
        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Slack, true, ("url", "")), registry, default);
        var afterBlank = (await repo.GetMany()).Single(r => r.ProviderType == NotificationChannelTypes.Slack);
        Assert.AreEqual("https://hooks.slack.com/services/ONE", StoredUrl(afterBlank), "blank secret preserves the stored value");

        // Re-save with a NEW secret — must replace.
        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Slack, true, ("url", "https://hooks.slack.com/services/TWO")), registry, default);
        var afterReplace = (await repo.GetMany()).Single(r => r.ProviderType == NotificationChannelTypes.Slack);
        Assert.AreEqual("https://hooks.slack.com/services/TWO", StoredUrl(afterReplace), "a non-blank secret replaces the stored value");
    }

    // ---- server-authoritative required-config validation (Hermes #362) ------

    [TestMethod]
    public async Task EnabledChannel_MissingRequiredSecret_OnFirstSave_IsRejected()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();

        // New profile, Slack enabled, no url and no stored secret → must be rejected (else it
        // would save "enabled" and silently never deliver).
        var reason = await LinkedTargetsAdminApi.ValidateSchemaChannelsAsync(repo, profileId: 0,
            Body(NotificationChannelTypes.Slack, enabled: true, ("url", "")), registry, default);
        Assert.IsNotNull(reason, "an enabled Slack channel with no webhook URL is rejected");
        StringAssert.Contains(reason!, "Slack");
    }

    [TestMethod]
    public async Task EnabledChannel_MissingNonSecretRequired_IsRejected()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();

        // Telegram enabled with a bot token but a blank (required, non-secret) chat id.
        var reason = await LinkedTargetsAdminApi.ValidateSchemaChannelsAsync(repo, profileId: 0,
            Body(NotificationChannelTypes.Telegram, enabled: true, ("botToken", "123:ABC"), ("chatId", "")), registry, default);
        Assert.IsNotNull(reason, "an enabled Telegram channel with no chat id is rejected");
        StringAssert.Contains(reason!, "Chat ID");
    }

    [TestMethod]
    public async Task DisabledChannel_MissingConfig_Passes()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();

        // A disabled channel is never validated — an operator may leave it unconfigured.
        var reason = await LinkedTargetsAdminApi.ValidateSchemaChannelsAsync(repo, profileId: 0,
            Body(NotificationChannelTypes.Slack, enabled: false, ("url", "")), registry, default);
        Assert.IsNull(reason);
    }

    [TestMethod]
    public async Task EnabledChannel_BlankSecret_WithStoredValue_Passes()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();
        var profileId = SeedProfile(db);

        // Store a secret first, then re-save with a masked (blank) secret — the effective config
        // preserves the stored value, so validation passes (the "leave blank to keep" case).
        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Slack, true, ("url", "https://hooks.slack.com/services/ONE")), registry, default);

        var reason = await LinkedTargetsAdminApi.ValidateSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Slack, true, ("url", "")), registry, default);
        Assert.IsNull(reason, "a blank secret with a stored credential passes validation");
    }

    [TestMethod]
    public async Task SchemalessType_IsSkipped_ByGenericPath()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();

        // "email" is not schema-driven (and not in this registry) — the generic path skips it;
        // email/web-push are handled by SyncChannelsAsync instead.
        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, 1,
            Body(NotificationChannelTypes.Email, true, ("recipients", "x@y.z")), registry, default);

        Assert.AreEqual(0, (await repo.GetMany()).Count, "no row created for a schemaless channel via the generic path");
    }

    // ---- #365: StoredSecretKeys projection (drives the editor's new-vs-stored secret UX) ----

    [TestMethod]
    public async Task StoredSecret_PopulatesStoredSecretKeys_ValueStillMasked()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();
        var profileId = SeedProfile(db);

        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Slack, true, ("url", "https://hooks.slack.com/services/SECRET")), registry, default);

        var projected = LinkedTargetsAdminApi.ProjectSchemaChannels((await repo.GetMany()).ToList(), registry);
        var slack = projected.Single(c => c.ProviderType == NotificationChannelTypes.Slack);
        CollectionAssert.Contains(slack.StoredSecretKeys, "url", "a stored secret is flagged so the editor shows 'leave blank to keep'");
        Assert.IsFalse(slack.Config.ContainsKey("url"), "the secret value itself is still never echoed");
    }

    [TestMethod]
    public async Task NoStoredSecret_LeavesStoredSecretKeysEmpty()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        using var db = NewDb(conn);
        var repo = new Repository<AlertProfileChannel>(db);
        var registry = Registry();
        var profileId = SeedProfile(db);

        // Telegram enabled with a chat id (non-secret) but NO bot token stored — the secret field
        // key must NOT be flagged (it's a new required secret, not a stored one).
        await LinkedTargetsAdminApi.PersistSchemaChannelsAsync(repo, profileId,
            Body(NotificationChannelTypes.Telegram, true, ("botToken", ""), ("chatId", "-100")), registry, default);

        var projected = LinkedTargetsAdminApi.ProjectSchemaChannels((await repo.GetMany()).ToList(), registry);
        var telegram = projected.Single(c => c.ProviderType == NotificationChannelTypes.Telegram);
        Assert.AreEqual(0, telegram.StoredSecretKeys.Count, "no stored secret ⇒ no key flagged (a new secret stays required)");
        Assert.AreEqual("-100", telegram.Config["chatId"], "the non-secret value still round-trips");
    }
}
