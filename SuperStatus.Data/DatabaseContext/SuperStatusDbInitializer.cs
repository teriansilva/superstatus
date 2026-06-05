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
    /// set of placeholder status checks. Sample-data seeding runs on dev by
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

    // Per-row seeds chosen so the Grid renderer's archetype picker
    // (issue #11 §5.2) lands across all four current archetypes:
    //   tenement (r<102), office-spire (102-165), megacorp-tower (166-203),
    //   karaoke-bar (≥204), where r = (Seed >>> 8) & 0xFF.
    // Ages spread across the tier curve so a city always shows growth.
    //
    // Seeding is ADDITIVE (issue #28). On every run we compare titles
    // against the existing StatusCheckSet and insert only the missing
    // ones — operator-added rows are preserved, and the sample set fills
    // out the city on staging without disturbing real data.
    private static async Task SeedStatusChecks(SuperStatusDb context)
    {
        DateTime now = DateTime.UtcNow;

        var samples = new List<StatusCheck>
        {
            Sample("Google",       "https://www.google.com",        0x125014, now.AddDays(-120), "Google search homepage"),
            Sample("GitHub",       "https://www.github.com",        0x238255, now.AddDays(-60),  "GitHub homepage"),
            Sample("Cloudflare",   "https://www.cloudflare.com",    0x34B419, now.AddDays(-200), "Cloudflare homepage"),
            Sample("Stripe",       "https://stripe.com",            0x45DC22, now.AddDays(-75),  "Stripe homepage"),
            Sample("GitLab",       "https://gitlab.com",            0x565003, now.AddDays(-28),  "GitLab homepage"),
            Sample("NPM",          "https://www.npmjs.com",         0x67B405, now.AddDays(-14),  "npm registry"),
            Sample("Sentry",       "https://sentry.io",             0x788247, now.AddDays(-5),   "Sentry"),
            Sample("Bing",         "https://www.bing.com",          0x89DC44, now.AddDays(-1),   "Bing search"),
            Sample("Mozilla",      "https://www.mozilla.org",       0x9A5111, now.AddDays(-95),  "Mozilla"),
            Sample("Wikipedia",    "https://www.wikipedia.org",     0xAB8231, now.AddDays(-180), "Wikipedia"),
            Sample("Reddit",       "https://www.reddit.com",        0xBCDC15, now.AddDays(-45),  "Reddit"),
            Sample("HackerNews",   "https://news.ycombinator.com",  0xCD5042, now.AddDays(-30),  "Hacker News"),
            Sample("StackOverflow","https://stackoverflow.com",     0xDEB418, now.AddDays(-100), "Stack Overflow"),
            Sample("ProductHunt",  "https://www.producthunt.com",   0xEF8226, now.AddDays(-22),  "Product Hunt"),
            Sample("Vercel",       "https://vercel.com",            0xF0DC09, now.AddDays(-15),  "Vercel"),
            Sample("Netlify",      "https://www.netlify.com",       0x115033, now.AddDays(-50),  "Netlify"),
            Sample("AWS",          "https://aws.amazon.com",        0x22B438, now.AddDays(-160), "AWS console"),
            Sample("Microsoft",    "https://www.microsoft.com",     0x338252, now.AddDays(-130), "Microsoft"),
            Sample("Notion",       "https://www.notion.so",         0x445014, now.AddDays(-8),   "Notion"),
            Sample("Figma",        "https://www.figma.com",         0x55DC03, now.AddDays(-40),  "Figma"),
            Sample("Linear",       "https://linear.app",            0x668249, now.AddDays(-12),  "Linear"),
            Sample("Slack",        "https://slack.com",             0x77501A, now.AddDays(-70),  "Slack"),
            Sample("Discord",      "https://discord.com",           0x88B412, now.AddDays(-90),  "Discord"),
            Sample("YouTube",      "https://www.youtube.com",       0x99B41D, now.AddDays(-220), "YouTube"),
            Sample("Spotify",      "https://www.spotify.com",       0xAA5030, now.AddDays(-110), "Spotify"),
            Sample("DuckDuckGo",   "https://duckduckgo.com",        0xBB825F, now.AddDays(-33),  "DuckDuckGo"),
            Sample("Hetzner",      "https://www.hetzner.com",       0xCCDC2E, now.AddDays(-18),  "Hetzner Cloud"),
            Sample("Anthropic",    "https://www.anthropic.com",     0xDD821A, now.AddDays(-7),   "Anthropic"),
            Sample("OpenAI",       "https://openai.com",            0xEEB407, now.AddDays(-65),  "OpenAI"),
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
    private static StatusCheck Sample(string title, string url, int seed, DateTime created, string description)
    {
        return new StatusCheck
        {
            Title = title,
            StatusCheckUrl = url,
            IsWebHookOnErrorEnabled = false,
            WebHookOnErrorUrl = "",
            ThrottleWebHookToExecuteOnlyEveryXMinutes = 5,
            ExpectedStatusCode = 200,
            ExpectedResponseTimeInMs = 1500,
            Description = description,
            Enabled = true,
            ServiceLogoUrl = "",
            Seed = seed,
            Created = created,
        };
    }
}
