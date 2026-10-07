using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.DatabaseContext;

/// <summary>
/// Database initializer for SuperStatus.
/// </summary>
public static class SuperStatusDbInitializer
{
    /// <summary>
    /// Optionally migrates the application database, then optionally seeds a
    /// placeholder status check. Sample-data seeding runs on dev by
    /// default and on other environments only when the operator opts in
    /// (SEED_SAMPLE_DATA=true).
    /// </summary>
    public static async Task Seed(
        IServiceProvider serviceProvider,
        bool applyMigrations,
        bool seedSampleData)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SuperStatusDb>();

        if (applyMigrations)
        {
            await dbContext.Database.MigrateAsync();
        }

        if (seedSampleData)
        {
            await SeedStatusChecks(dbContext);
        }
    }

    //
    // Seeding is ADDITIVE (issue #28). On every run we compare titles
    // against the existing StatusCheckSet and insert only the missing
    // ones — operator-added rows are preserved.
    private static async Task SeedStatusChecks(SuperStatusDb context)
    {
        DateTime now = DateTime.UtcNow;

        var samples = new List<StatusCheck>
        {
            Sample("Google",       "https://www.google.com",        now.AddDays(-120), "Google search homepage"),
        };

        var existingTitles = await context.StatusCheckSet
            .Select(x => x.Title)
            .ToListAsync();
        var existingSet = new HashSet<string>(existingTitles, StringComparer.OrdinalIgnoreCase);

        int inserted = 0;
        foreach (var s in samples)
        {
            if (existingSet.Contains(s.Title)) continue;
            context.StatusCheckSet.Add(s);
            inserted++;
        }

        if (inserted > 0)
        {
            await context.SaveChangesAsync();
        }
    }

    // Helper so the seed list above stays readable. No explicit Id —
    // EF auto-assigns from the IdentityByDefault column, so seeding on
    // a non-empty database does NOT collide with operator-added rows.
    private static StatusCheck Sample(string title, string url, DateTime created, string description)
    {
        // No SlaId here — the seed runs before the #293 startup backfill,
        // which assigns every SLA-less check to the default SLA.
        return new StatusCheck
        {
            Title = title,
            StatusCheckUrl = url,
            ExpectedStatusCode = 200,
            Description = description,
            Enabled = true,
            ServiceLogoUrl = "",
            Created = created,
        };
    }
}
