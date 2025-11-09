using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.DatabaseContext;

/// <summary>
/// Database initializer for SuperTalk.
/// </summary>
public static class SuperStatusDbInitializer
{
    /// <summary>
    /// Seeds the database with initial data if it is empty.
    /// This method should be called during application startup to ensure the database is ready for use.
    /// </summary>
    public static async Task Seed(IServiceProvider serviceProvider, bool isDevEnvironment)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SuperStatusDb>();

        if (isDevEnvironment)
        {
            await dbContext.Database.MigrateAsync();
        }
        await SeedConfiguration(dbContext);
        await SeedStatusChecks(dbContext);
    }
    
    private static async Task SeedConfiguration(SuperStatusDb context)
    {
        if (context.ConfigurationSet.Any())
        {
            return; // Configuration has been seeded
        }

        var configuration = new Configuration
        {
            Id = 1,
            Title = "SuperStatus",
            Description = "System Status Dashboard",
            LogoUrl = "/logo.png",
            Favicon = "/favicon.ico",
            Theme = "light",
            ShowSupportLink = true,
            JobIntervallInSeconds = 60,
            DbCleanUpJobIntervallInMinutes = 1440,
            JobName = "StatusCheckJob",
            RunJobAtStartup = true,
            StatusCheckViewRefreshIntervalInSeconds = 30,
            StatusCheckGraphViewMaxDays = 30,
            ShowSlowResponseTimeInGraph = true
        };

        context.ConfigurationSet.Add(configuration);
        await context.SaveChangesAsync();
    }

    private static async Task SeedStatusChecks(SuperStatusDb context)
    {
        if (context.StatusCheckSet.Any())
        {
            return; // DB has been seeded
        }

        // clear status check database
        context.StatusCheckSet.RemoveRange(context.StatusCheckSet);
        await context.SaveChangesAsync();

        var statusChecks = new List<StatusCheck>
            {
                new StatusCheck
                {
                    Id = 1,
                    Title = "Google",
                    StatusCheckUrl = "https://www.google.com",
                    IsWebHookOnErrorEnabled = true,
                    WebHookOnErrorUrl = "https://example.com/webhook",
                    ThrottleWebHookToExecuteOnlyEveryXMinutes = 5,
                    ExpectedStatusCode = 200,
                    ExpectedResponseTimeInMs = 1000,
                    Description = "Google's homepage",
                    Enabled = true,
                    ServiceLogoUrl = "https://www.google.com/images/branding/googlelogo/1x/googlelogo_color_272x92dp.png"
                },
                new StatusCheck
                {
                    Id = 2,
                    Title = "GitHub",
                    StatusCheckUrl = "https://www.github.com",
                    IsWebHookOnErrorEnabled = true,
                    WebHookOnErrorUrl = "https://example.com/webhook",
                    ThrottleWebHookToExecuteOnlyEveryXMinutes = 5,
                    ExpectedStatusCode = 200,
                    ExpectedResponseTimeInMs = 1000,
                    Description = "GitHub's homepage",
                    Enabled = true,
                    ServiceLogoUrl = "https://github.githubassets.com/images/modules/logos_page/GitHub-Mark.png"
                },
            };
        context.StatusCheckSet.AddRange(statusChecks);
        await context.SaveChangesAsync();
    }

}