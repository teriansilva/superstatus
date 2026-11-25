using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #167 — the site-settings singleton store. Verifies seed-once,
/// the single-row invariant, and accent/logo validation, against a relational
/// SQLite provider.
/// </summary>
[TestClass]
public class SiteSettingsTests
{
    private static (SiteSettingsService svc, SuperStatusDb db, SqliteConnection conn) Build()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (new SiteSettingsService(new SiteSettingsRepository(db)), db, conn);
    }

    [TestMethod]
    public async Task Get_SeedsSingletonOnce_NeverASecondRow()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var first = await svc.GetSettingsAsync();
        Assert.AreEqual(SiteSettingsService.DefaultAccent, first.AccentColor, "seeds the HUD default accent");
        Assert.AreEqual(1, await db.SiteSettingsSet.CountAsync());

        // The fresh-install brand is the product itself — never a deployment-
        // specific name carried in from config (regression: seeded "Acme…").
        Assert.AreEqual(SiteSettingsService.DefaultTitle, first.Title, "seeds the product title");
        Assert.AreEqual(SiteSettingsService.DefaultSubtitle, first.Subtitle, "seeds the product subtitle");

        // A second read must reuse the same row — no duplicate "global" settings.
        await svc.GetSettingsAsync();
        await svc.SaveSettingsAsync(new SiteSettingsViewModel { Title = "x", AccentColor = "#112233" });
        await svc.GetSettingsAsync();
        Assert.AreEqual(1, await db.SiteSettingsSet.CountAsync(), "the settings row is a singleton");
        Assert.AreEqual(SuperStatus.Data.Entities.SiteSettings.SingletonId,
            (await db.SiteSettingsSet.SingleAsync()).Id);
    }

    [TestMethod]
    public async Task Save_PersistsValidValues_LowercasesAccent()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var saved = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "  Acme Status  ",
            LogoUrl = "https://cdn.example.com/logo.png",
            AccentColor = "#AABBCC",
        });

        Assert.AreEqual("Acme Status", saved.Title, "title trimmed");
        Assert.AreEqual("https://cdn.example.com/logo.png", saved.LogoUrl);
        Assert.AreEqual("#aabbcc", saved.AccentColor, "valid hex normalized to lower-case");

        // Survives a reload (persisted, single row).
        var reread = await svc.GetSettingsAsync();
        Assert.AreEqual("Acme Status", reread.Title);
        Assert.AreEqual("#aabbcc", reread.AccentColor);
    }

    [TestMethod]
    public async Task Save_RejectsBadAccent_AndNonHttpLogoScheme()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var saved = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T",
            AccentColor = "red; drop table",        // not #rrggbb
            LogoUrl = "javascript:alert(1)",         // dangerous scheme
        });

        Assert.AreEqual(SiteSettingsService.DefaultAccent, saved.AccentColor, "invalid accent → HUD default");
        Assert.AreEqual(string.Empty, saved.LogoUrl, "non-http(s) logo scheme → cleared");
    }

    [TestMethod]
    public async Task SecondRow_DirectInsert_RejectedByCheckConstraint()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        await svc.GetSettingsAsync(); // seeds the id=1 row

        // The CHECK ("Id" = 1) makes a second settings row impossible at the
        // persistence boundary, not just in the service path.
        db.SiteSettingsSet.Add(new SuperStatus.Data.Entities.SiteSettings { Id = 2, Title = "rogue", AccentColor = "#3fbf6f" });
        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Save_AcceptsEmptyLogo()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var saved = await svc.SaveSettingsAsync(new SiteSettingsViewModel { Title = "T", LogoUrl = "", AccentColor = "#3fbf6f" });
        Assert.AreEqual(string.Empty, saved.LogoUrl);
    }

    [TestMethod]
    public async Task Onboarding_NullUntilCompleted_ThenStampedOnce()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var fresh = await svc.GetSettingsAsync();
        Assert.IsNull(fresh.OnboardedUtc, "a freshly-seeded install is not onboarded");

        var done = await svc.CompleteOnboardingAsync();
        Assert.IsNotNull(done.OnboardedUtc, "completing onboarding stamps the time");
        var stamp = done.OnboardedUtc;

        // Idempotent: a second call must not move the timestamp.
        var again = await svc.CompleteOnboardingAsync();
        Assert.AreEqual(stamp, again.OnboardedUtc);
        Assert.AreEqual(stamp, (await svc.GetSettingsAsync()).OnboardedUtc, "persisted + stable");
    }

    [TestMethod]
    public async Task Save_PersistsTrimmedSubtitle()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var saved = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "TEST", Subtitle = "  Acme Service Status Information  ", AccentColor = "#3fbf6f",
        });
        Assert.AreEqual("Acme Service Status Information", saved.Subtitle, "subtitle trimmed");

        var reread = await svc.GetSettingsAsync();
        Assert.AreEqual("Acme Service Status Information", reread.Subtitle);
    }

    // ---- #170: footer settings ----

    [TestMethod]
    public async Task Get_SeedsFooterDefault_AndShowAdminLinkOn()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var s = await svc.GetSettingsAsync();
        Assert.AreEqual(SiteSettingsService.DefaultFooterText, s.FooterText, "footer seeds the prior classification text");
        Assert.AreEqual(0, s.FooterLinks.Count);
        Assert.IsTrue(s.ShowAdminLink, "admin link shown by default");
    }

    [TestMethod]
    public async Task Save_FooterLinks_DropInvalid_TrimLabels_AndPersist()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var saved = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T",
            AccentColor = "#3fbf6f",
            FooterText = "  © 2026 Acme  ",
            ShowAdminLink = false,
            FooterLinks = new()
            {
                new FooterLink { Label = "  Privacy  ", Url = "https://www.example.com/privacy" },
                new FooterLink { Label = "Bad scheme", Url = "javascript:alert(1)" }, // dropped
                new FooterLink { Label = "", Url = "https://example.com" },            // dropped (no label)
            },
        });

        Assert.AreEqual("© 2026 Acme", saved.FooterText, "footer text trimmed");
        Assert.IsFalse(saved.ShowAdminLink, "toggle persisted");
        Assert.AreEqual(1, saved.FooterLinks.Count, "only the valid http(s) + labelled link survives");
        Assert.AreEqual("Privacy", saved.FooterLinks[0].Label, "label trimmed");
        Assert.AreEqual("https://www.example.com/privacy", saved.FooterLinks[0].Url);

        // Round-trips from storage (JSON column).
        var reread = await svc.GetSettingsAsync();
        Assert.AreEqual(1, reread.FooterLinks.Count);
        Assert.AreEqual("Privacy", reread.FooterLinks[0].Label);
        Assert.IsFalse(reread.ShowAdminLink);
    }

    [TestMethod]
    public async Task Save_FooterLinks_CapsAtEight()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var many = new SiteSettingsViewModel { Title = "T", AccentColor = "#3fbf6f" };
        for (int i = 0; i < 12; i++)
            many.FooterLinks.Add(new FooterLink { Label = $"L{i}", Url = $"https://example.com/{i}" });

        var saved = await svc.SaveSettingsAsync(many);
        Assert.AreEqual(SiteSettingsService.MaxFooterLinks, saved.FooterLinks.Count, "link list is capped");
    }

    [TestMethod]
    public async Task Get_ReadPath_DropsNonHttpFooterLink_FromPersistedJson()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        // A hand-edited / non-service-written row carrying a dangerous scheme —
        // the read path must still drop it before it reaches the public footer.
        db.SiteSettingsSet.Add(new SuperStatus.Data.Entities.SiteSettings
        {
            Id = SuperStatus.Data.Entities.SiteSettings.SingletonId,
            AccentColor = "#3fbf6f",
            FooterText = "x",
            FooterLinksJson = "[{\"label\":\"Evil\",\"url\":\"javascript:alert(1)\"},{\"label\":\"OK\",\"url\":\"https://ok.example.com\"}]",
            ShowAdminLink = true,
        });
        await db.SaveChangesAsync();

        var s = await svc.GetSettingsAsync();
        Assert.AreEqual(1, s.FooterLinks.Count, "the javascript: link is dropped on read");
        Assert.AreEqual("https://ok.example.com", s.FooterLinks[0].Url);
    }

    // ---- #168: AI / automation settings ----

    [TestMethod]
    public async Task Save_PersistsAiConfig_AndEnables()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var saved = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f",
            AiEnabled = true, AiBaseUrl = "https://gw.example.com/v1", AiModel = "gpt-4o-mini",
            AiApiKey = "sk-secret", AiTimeoutSeconds = 30, AutoIncidentThresholdMinutes = 10,
        });

        Assert.IsTrue(saved.AiEnabled, "valid config stays enabled");
        Assert.AreEqual("https://gw.example.com/v1", saved.AiBaseUrl);
        Assert.AreEqual("gpt-4o-mini", saved.AiModel);
        Assert.AreEqual(30, saved.AiTimeoutSeconds);
        Assert.AreEqual(10, saved.AutoIncidentThresholdMinutes);
    }

    [TestMethod]
    public async Task AiApiKey_IsWriteOnly_NeverEchoed_ButSetFlagSurfaces()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var saved = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f",
            AiEnabled = true, AiBaseUrl = "https://gw/v1", AiModel = "m", AiApiKey = "sk-topsecret",
        });
        Assert.IsNull(saved.AiApiKey, "the key is never echoed back");
        Assert.IsTrue(saved.AiApiKeySet, "but the UI is told a key is stored");

        var reread = await svc.GetSettingsAsync();
        Assert.IsNull(reread.AiApiKey);
        Assert.IsTrue(reread.AiApiKeySet);
        // The raw row holds the real key (plaintext for now — encrypted-at-rest is a follow-up).
        Assert.AreEqual("sk-topsecret", (await db.SiteSettingsSet.SingleAsync()).AiApiKey);
    }

    [TestMethod]
    public async Task Save_BlankKey_PreservesStoredKey_NonBlankReplaces()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f", AiEnabled = true, AiBaseUrl = "https://gw/v1", AiModel = "m", AiApiKey = "sk-first",
        });

        // A save with a null key (the UI default — never round-trips the secret) keeps the stored key.
        await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T2", AccentColor = "#3fbf6f", AiEnabled = true, AiBaseUrl = "https://gw/v1", AiModel = "m", AiApiKey = null,
        });
        Assert.AreEqual("sk-first", (await db.SiteSettingsSet.SingleAsync()).AiApiKey, "blank submission preserves the key");

        // A non-blank value replaces it.
        await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T3", AccentColor = "#3fbf6f", AiEnabled = true, AiBaseUrl = "https://gw/v1", AiModel = "m", AiApiKey = "sk-second",
        });
        Assert.AreEqual("sk-second", (await db.SiteSettingsSet.SingleAsync()).AiApiKey, "a non-blank value replaces it");
    }

    [TestMethod]
    public async Task Save_AiEnabled_NormalizedOff_WhenConfigIncomplete()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        // Enabled requested but no base URL / model → can't enable.
        var noConfig = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f", AiEnabled = true, AiBaseUrl = "", AiModel = "",
        });
        Assert.IsFalse(noConfig.AiEnabled, "no base URL / model → enable is normalized off");

        // A non-http(s) base URL is rejected (cleared) → still can't enable.
        var badUrl = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f", AiEnabled = true, AiBaseUrl = "ftp://gw/v1", AiModel = "m",
        });
        Assert.AreEqual(string.Empty, badUrl.AiBaseUrl, "non-http(s) base URL is dropped");
        Assert.IsFalse(badUrl.AiEnabled);
    }

    [TestMethod]
    public async Task Save_ClampsTimeoutAndThreshold()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var low = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f", AiTimeoutSeconds = 1, AutoIncidentThresholdMinutes = 0,
        });
        // timeout floors to the min; a non-positive threshold falls back to the default.
        Assert.AreEqual(SiteSettingsService.MinAiTimeoutSeconds, low.AiTimeoutSeconds);
        Assert.AreEqual(SiteSettingsService.DefaultThresholdMinutes, low.AutoIncidentThresholdMinutes);

        var high = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f", AiTimeoutSeconds = 9999, AutoIncidentThresholdMinutes = 99999,
        });
        Assert.AreEqual(SiteSettingsService.MaxAiTimeoutSeconds, high.AiTimeoutSeconds);
        Assert.AreEqual(SiteSettingsService.MaxThresholdMinutes, high.AutoIncidentThresholdMinutes);
    }

    [TestMethod]
    public async Task Prompt_DefaultSurfacedWhenUnset_CustomPersisted_DefaultStoredBlank()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        // Fresh install: GET surfaces the built-in default so the operator sees it.
        var fresh = await svc.GetSettingsAsync();
        Assert.AreEqual(SiteSettingsService.DefaultAiPrompt, fresh.AiPromptTemplate);

        // A custom prompt round-trips.
        var custom = "Draft an incident for {service}. JSON only.";
        var saved = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f", AiPromptTemplate = custom,
        });
        Assert.AreEqual(custom, saved.AiPromptTemplate);
        Assert.AreEqual(custom, (await db.SiteSettingsSet.SingleAsync()).AiPromptTemplate);

        // Submitting a value equal to the default stores blank (so the row follows
        // the evolving built-in default) but GET still surfaces the default text.
        var reset = await svc.SaveSettingsAsync(new SiteSettingsViewModel
        {
            Title = "T", AccentColor = "#3fbf6f", AiPromptTemplate = SiteSettingsService.DefaultAiPrompt,
        });
        Assert.AreEqual(string.Empty, (await db.SiteSettingsSet.SingleAsync()).AiPromptTemplate, "default-equal stored blank");
        Assert.AreEqual(SiteSettingsService.DefaultAiPrompt, reset.AiPromptTemplate, "GET still surfaces the default");
    }

    [TestMethod]
    public async Task GetOrCreateVapidPublicKey_generatesOnce_andIsStableAcrossCalls()
    {
        // #241 Phase C: first call provisions + persists a VAPID pair (private key
        // stored server-side, never echoed); subsequent calls reuse the same key.
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var first = await svc.GetOrCreateVapidPublicKeyAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(first), "a public key is returned");

        // Read no-tracking: the atomic set is an ExecuteUpdate (bypasses the change
        // tracker), so a previously-tracked row would be stale — assert the DB state.
        var row = await db.SiteSettingsSet.AsNoTracking().SingleAsync();
        Assert.AreEqual(first, row.VapidPublicKey);
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.VapidPrivateKey), "the private key is persisted server-side");

        var second = await svc.GetOrCreateVapidPublicKeyAsync();
        Assert.AreEqual(first, second, "the key is generated once and reused");
        Assert.AreEqual(1, await db.SiteSettingsSet.AsNoTracking().CountAsync(), "still the singleton row");

        // Neither VAPID key leaks onto the general settings view-model.
        var vm = await svc.GetSettingsAsync();
        Assert.IsFalse(vm.GetType().GetProperties().Any(p => p.Name.StartsWith("Vapid")),
            "no VAPID property is exposed on the settings view-model");
    }

    [TestMethod]
    public async Task SetVapidKeysIfAbsent_isCompareAndSet_firstWins()
    {
        // #241 Phase C: the persistence-level compare-and-set — sets when the pair is
        // absent, is a no-op once present, and never overwrites the first pair.
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var repo = new SiteSettingsRepository(db);
        db.SiteSettingsSet.Add(new SiteSettings { Id = SiteSettings.SingletonId, UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        Assert.IsTrue(await repo.SetVapidKeysIfAbsentAsync("PUB1", "PRIV1", DateTime.UtcNow), "sets when absent");
        Assert.IsFalse(await repo.SetVapidKeysIfAbsentAsync("PUB2", "PRIV2", DateTime.UtcNow), "no-op when already present");

        var row = await db.SiteSettingsSet.AsNoTracking().SingleAsync();
        Assert.AreEqual("PUB1", row.VapidPublicKey, "the first pair is preserved");
        Assert.AreEqual("PRIV1", row.VapidPrivateKey);
        Assert.AreEqual("PUB1", await repo.GetVapidPublicKeyAsync());
    }

    [TestMethod]
    public async Task GetOrCreateVapidPublicKey_underConcurrency_allCallersGetTheOnePersistedPair()
    {
        // #241 Phase C regression (Hermes #263): two first-use callers racing must not
        // clobber each other — exactly one pair is persisted and BOTH receive it.
        const string cs = "Data Source=vapidrace;Mode=Memory;Cache=Shared";
        // Hold one connection open so the shared in-memory database survives the test.
        using var keepAlive = new SqliteConnection(cs);
        keepAlive.Open();

        SuperStatusDb NewDb() => new(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(cs).Options);

        using (var seed = NewDb())
        {
            seed.Database.EnsureCreated();
            seed.SiteSettingsSet.Add(new SiteSettings { Id = SiteSettings.SingletonId, UpdatedUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        async Task<string> Call()
        {
            using var db = NewDb();
            var svc = new SiteSettingsService(new SiteSettingsRepository(db));
            return await svc.GetOrCreateVapidPublicKeyAsync();
        }

        var results = await Task.WhenAll(Call(), Call());

        Assert.IsFalse(string.IsNullOrWhiteSpace(results[0]), "a key is returned");
        Assert.AreEqual(1, results.Distinct().Count(), "racing callers all get the same persisted public key");

        using var check = NewDb();
        var row = await check.SiteSettingsSet.AsNoTracking().SingleAsync();
        Assert.AreEqual(results[0], row.VapidPublicKey, "the returned key is the one persisted");
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.VapidPrivateKey), "a matching private key is persisted");
    }
}
