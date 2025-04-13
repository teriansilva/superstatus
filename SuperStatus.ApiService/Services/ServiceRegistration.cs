using Microsoft.EntityFrameworkCore;
using Quartz;
using SuperStatus.ApiService.Configuration;
using SuperStatus.Configuration;
using SuperStatus.Data.Repositories;
using SuperStatus.Scheduler;


namespace SuperStatus.Services
{
    /// <summary>
    /// Initializes all the require application services
    /// </summary>
    public static class ServiceRegistration
    {
        public static void AddApplicationServices(this IServiceCollection services, WebApplicationBuilder builder)
        {

            // SQLite Database context
            services.AddDbContext<SuperStatusContext>(options =>
            {
                options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"), b => b.MigrationsAssembly("SuperStatus.ApiService"));
            });

            services.AddHttpClient();

            if (SuperStatusConfig.RunJobAtStartup)
            {

                builder.Services.AddQuartz(q =>
                {
                    q.SchedulerId = "JobScheduler";
                    q.SchedulerName = "Job Scheduler";
                    q.AddJob<SuperStatusCheckJob>(opts => opts.WithIdentity(SuperStatusConfig.JobName));
                    q.AddJob<SuperStatusCleanUpJob>(opts => opts.WithIdentity(typeof(SuperStatusCleanUpJob).Name));
                    q.AddTrigger(opts => opts
                        .ForJob(SuperStatusConfig.JobName)
                        .WithIdentity($"{SuperStatusConfig.JobName}-interval")
                        .WithDescription("Status Check default job")
                        .WithSimpleSchedule(x => x
                            .WithIntervalInSeconds(SuperStatusConfig.JobIntervallInSeconds)
                            .RepeatForever())
                    );
                    q.AddTrigger(opts => opts
                        .ForJob(typeof(SuperStatusCleanUpJob).Name)
                        .WithIdentity($"{typeof(SuperStatusCleanUpJob).Name}-interval")
                        .WithDescription("Status Check db cleanup job")
                        .WithSimpleSchedule(x => x
                            .WithIntervalInMinutes(SuperStatusConfig.DbCleanUpJobIntervallInMinutes)
                            .RepeatForever())
                    );

                });

                builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
            }
            // Add custom services
            services.AddTransient<SuperStatusSeeder>();
            services.AddTransient<IDbContextFactory<SuperStatusContext>, SuperDbContextFactory<SuperStatusContext>>();
            services.AddScoped<ISuperStatusRepository, SuperStatusRepository>();
            services.AddScoped<IStatusCheckService, StatusCheckService>();

        }
    }
}
