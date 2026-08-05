using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #375: first-run sample-data seeding should create one calm demo check,
/// not a grid of external services.
/// </summary>
[TestClass]
public class SuperStatusDbInitializerTests
{
    private static ServiceProvider BuildProvider(SqliteConnection conn)
    {
        var services = new ServiceCollection();
        services.AddSingleton(conn);
        services.AddDbContext<SuperStatusDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()), ServiceLifetime.Scoped);
        return services.BuildServiceProvider();
    }

    private static async Task EnsureCreatedAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SuperStatusDb>().Database.EnsureCreatedAsync();
    }

    private static async Task<List<StatusCheck>> ChecksAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SuperStatusDb>().StatusCheckSet
            .AsNoTracking()
            .OrderBy(c => c.Title)
            .ToListAsync();
    }

    [TestMethod]
    public async Task SeedSampleData_FreshDb_InsertsOnlyGoogleDemoCheck()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var provider = BuildProvider(conn);
        await EnsureCreatedAsync(provider);

        await SuperStatusDbInitializer.Seed(provider, applyMigrations: false, seedSampleData: true);

        var checks = await ChecksAsync(provider);
        Assert.AreEqual(1, checks.Count, "fresh sample-data seed should create exactly one demo check");
        var google = checks.Single();
        Assert.AreEqual("Google", google.Title);
        Assert.AreEqual("https://www.google.com", google.StatusCheckUrl);
        Assert.AreEqual(200, google.ExpectedStatusCode);
        Assert.IsTrue(google.Enabled);
    }

    [TestMethod]
    public async Task SeedSampleData_IsIdempotent_AndPreservesOperatorChecks()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var provider = BuildProvider(conn);
        await EnsureCreatedAsync(provider);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SuperStatusDb>();
            db.StatusCheckSet.Add(new StatusCheck
            {
                Title = "Operator API",
                StatusCheckUrl = "https://api.example.com/health",
                ExpectedStatusCode = 200,
                Enabled = true,
                ServiceLogoUrl = string.Empty,
                Created = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await SuperStatusDbInitializer.Seed(provider, applyMigrations: false, seedSampleData: true);
        await SuperStatusDbInitializer.Seed(provider, applyMigrations: false, seedSampleData: true);

        var checks = await ChecksAsync(provider);
        Assert.AreEqual(2, checks.Count, "seeding should add Google once and keep operator rows");
        Assert.AreEqual(1, checks.Count(c => c.Title == "Google"), "Google seed is title-idempotent");
        Assert.AreEqual(1, checks.Count(c => c.Title == "Operator API"), "operator-created check is preserved");
    }
}
