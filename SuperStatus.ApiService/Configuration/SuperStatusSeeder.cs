using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.ApiService.Configuration
{
    public class SuperStatusSeeder
    {
        private readonly SuperStatusContext context;
        private readonly IWebHostEnvironment hosting;

        public SuperStatusSeeder(SuperStatusContext context, IWebHostEnvironment hosting)
        {

            this.hosting = hosting;
            this.context = context;
        }

        public async Task SeedAsync()
        {
            context.Database.EnsureCreated();
            await SeedStatusChecks();
        }

        public async Task SeedStatusChecks()
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
}
