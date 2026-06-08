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

    //
    // Seeding is ADDITIVE (issue #28). On every run we compare titles
    // against the existing StatusCheckSet and insert only the missing
    // ones — operator-added rows are preserved, and the sample set fills
    // out on staging without disturbing real data.
    private static async Task SeedStatusChecks(SuperStatusDb context)
    {
        DateTime now = DateTime.UtcNow;

        var samples = new List<StatusCheck>
        {
            Sample("Google",       "https://www.google.com",        now.AddDays(-120), "Google search homepage"),
            Sample("GitHub",       "https://www.github.com",        now.AddDays(-60),  "GitHub homepage"),
            Sample("Cloudflare",   "https://www.cloudflare.com",    now.AddDays(-200), "Cloudflare homepage"),
            Sample("Stripe",       "https://stripe.com",            now.AddDays(-75),  "Stripe homepage"),
            Sample("GitLab",       "https://gitlab.com",            now.AddDays(-28),  "GitLab homepage"),
            Sample("NPM",          "https://www.npmjs.com",         now.AddDays(-14),  "npm registry"),
            Sample("Sentry",       "https://sentry.io",             now.AddDays(-5),   "Sentry"),
            Sample("Bing",         "https://www.bing.com",          now.AddDays(-1),   "Bing search"),
            Sample("Mozilla",      "https://www.mozilla.org",       now.AddDays(-95),  "Mozilla"),
            Sample("Wikipedia",    "https://www.wikipedia.org",     now.AddDays(-180), "Wikipedia"),
            Sample("Reddit",       "https://www.reddit.com",        now.AddDays(-45),  "Reddit"),
            Sample("HackerNews",   "https://news.ycombinator.com",  now.AddDays(-30),  "Hacker News"),
            Sample("StackOverflow","https://stackoverflow.com",     now.AddDays(-100), "Stack Overflow"),
            Sample("ProductHunt",  "https://www.producthunt.com",   now.AddDays(-22),  "Product Hunt"),
            Sample("Vercel",       "https://vercel.com",            now.AddDays(-15),  "Vercel"),
            Sample("Netlify",      "https://www.netlify.com",       now.AddDays(-50),  "Netlify"),
            Sample("AWS",          "https://aws.amazon.com",        now.AddDays(-160), "AWS console"),
            Sample("Microsoft",    "https://www.microsoft.com",     now.AddDays(-130), "Microsoft"),
            Sample("Notion",       "https://www.notion.so",         now.AddDays(-8),   "Notion"),
            Sample("Figma",        "https://www.figma.com",         now.AddDays(-40),  "Figma"),
            Sample("Linear",       "https://linear.app",            now.AddDays(-12),  "Linear"),
            Sample("Slack",        "https://slack.com",             now.AddDays(-70),  "Slack"),
            Sample("Discord",      "https://discord.com",           now.AddDays(-90),  "Discord"),
            Sample("YouTube",      "https://www.youtube.com",       now.AddDays(-220), "YouTube"),
            Sample("Spotify",      "https://www.spotify.com",       now.AddDays(-110), "Spotify"),
            Sample("DuckDuckGo",   "https://duckduckgo.com",        now.AddDays(-33),  "DuckDuckGo"),
            Sample("Hetzner",      "https://www.hetzner.com",       now.AddDays(-18),  "Hetzner Cloud"),
            Sample("Anthropic",    "https://www.anthropic.com",     now.AddDays(-7),   "Anthropic"),
            Sample("OpenAI",       "https://openai.com",            now.AddDays(-65),  "OpenAI"),
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
            Created = created,
        };
    }
}
