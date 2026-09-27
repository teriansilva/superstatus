using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Scheduler;
using SuperStatus.Services.Services;
using SuperStatus.Services.Updates;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #249 (epic #248): the persisted update-check state on the SiteSettings
/// singleton, and the worker cycle honouring the enabled toggle — against SQLite.
/// </summary>
[TestClass]
public class UpdateCheckPersistenceTests
{
    private sealed class StubChecker : IUpdateCheckService
    {
        public int Calls { get; private set; }
        public UpdateCheckResult Result { get; set; } =
            new(UpdateStatus.UpdateAvailable, "1.0.0", "1.2.0", "https://notes", null, DateTime.UtcNow);

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private static (ISiteSettingsService svc, SuperStatusDb db, SqliteConnection conn) Build()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (new SiteSettingsService(new SiteSettingsRepository(db)), db, conn);
    }

    [TestMethod]
    public async Task FreshInstall_defaultsToEnabledNeverChecked()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;
        var state = await svc.GetUpdateCheckStateAsync();
        Assert.IsTrue(state.Enabled, "checks default on");
        Assert.IsNull(state.LastCheckedUtc);
        Assert.IsNull(state.LatestVersion);
        Assert.IsNull(state.LastCheckError);
    }

    [TestMethod]
    public async Task SetResult_success_thenFailure_keepsLastKnownVersionAndRecordsError()
    {
        var (svc, db, conn) = Build(); using var _ = db; using var __ = conn;

        var t1 = DateTime.UtcNow;
        await svc.SetUpdateCheckResultAsync("1.2.0", "https://notes", error: null, checkedUtc: t1);
        var ok = await svc.GetUpdateCheckStateAsync();
        Assert.AreEqual("1.2.0", ok.LatestVersion);
        Assert.AreEqual("https://notes", ok.LatestNotesUrl);
        Assert.IsNull(ok.LastCheckError);
        Assert.AreEqual(t1, ok.LastCheckedUtc);

        // A later failed check must not wipe the last-known-good version.
        var t2 = t1.AddHours(1);
        await svc.SetUpdateCheckResultAsync(latestVersion: null, latestNotesUrl: null, error: "boom", checkedUtc: t2);
        var failed = await svc.GetUpdateCheckStateAsync();
        Assert.AreEqual("1.2.0", failed.LatestVersion, "keeps the last good version");
        Assert.AreEqual("boom", failed.LastCheckError);
        Assert.AreEqual(t2, failed.LastCheckedUtc);

        Assert.AreEqual(1, await db.SiteSettingsSet.CountAsync(), "still a singleton");
    }

    [TestMethod]
    public async Task WorkerCycle_whenEnabled_runsCheckAndPersists()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var _ = conn;
        using var seed = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        seed.Database.EnsureCreated();

        var checker = new StubChecker();
        var provider = BuildProvider(conn, checker);

        await UpdateCheckWorker.RunCycleAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, default);

        Assert.AreEqual(1, checker.Calls, "enabled → the check runs");
        using var scope = provider.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<ISiteSettingsService>().GetUpdateCheckStateAsync();
        Assert.AreEqual("1.2.0", state.LatestVersion, "result persisted");
    }

    [TestMethod]
    public async Task WorkerCycle_onEmptyDb_stillSeedsBrandingDefaults()
    {
        // Regression (#251): the worker can persist the first update result before
        // the operator ever opens /settings. Persisting must seed the FULL singleton,
        // not a sparse row — otherwise GetSettingsAsync stops seeding branding/footer
        // defaults afterwards.
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var _ = conn;
        using var seed = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        seed.Database.EnsureCreated();

        var provider = BuildProvider(conn, new StubChecker());
        await UpdateCheckWorker.RunCycleAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, default);

        using var scope = provider.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISiteSettingsService>();
        var branding = await settings.GetSettingsAsync();
        Assert.AreEqual(SiteSettingsService.DefaultTitle, branding.Title, "branding still seeded after the worker wrote first");
        Assert.AreEqual(SiteSettingsService.DefaultSubtitle, branding.Subtitle);
        Assert.AreEqual(SiteSettingsService.DefaultFooterText, branding.FooterText);
        Assert.AreEqual(SiteSettingsService.DefaultAccent, branding.AccentColor);

        // ...and the update result it wrote is still present (single row, not duplicated).
        var state = await settings.GetUpdateCheckStateAsync();
        Assert.AreEqual("1.2.0", state.LatestVersion);
        var db = scope.ServiceProvider.GetRequiredService<SuperStatusDb>();
        Assert.AreEqual(1, await db.SiteSettingsSet.CountAsync(), "still a singleton");
    }

    [TestMethod]
    public async Task WorkerCycle_whenDisabled_skipsTheCheck()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var _ = conn;
        using var seed = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        seed.Database.EnsureCreated();
        seed.SiteSettingsSet.Add(new SiteSettings { Id = SiteSettings.SingletonId, UpdateCheckEnabled = false });
        await seed.SaveChangesAsync();

        var checker = new StubChecker();
        var provider = BuildProvider(conn, checker);

        await UpdateCheckWorker.RunCycleAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, default);

        Assert.AreEqual(0, checker.Calls, "disabled → the check is skipped");
    }

    private static ServiceProvider BuildProvider(SqliteConnection conn, IUpdateCheckService checker)
    {
        var services = new ServiceCollection();
        services.AddSingleton(conn);
        services.AddScoped(sp => new SuperStatusDb(
            new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(sp.GetRequiredService<SqliteConnection>()).Options));
        services.AddScoped<ISiteSettingsRepository, SiteSettingsRepository>();
        services.AddScoped<ISiteSettingsService, SiteSettingsService>();
        services.AddSingleton(checker);
        return services.BuildServiceProvider();
    }
}
