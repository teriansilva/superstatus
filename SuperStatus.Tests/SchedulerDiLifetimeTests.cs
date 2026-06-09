using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Scheduler;
using SuperStatus.Services.Scheduling;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #84 (Hermes review) — the tick orchestrators are registered as
/// singletons (the hosted services are singletons), so they must not capture
/// scoped repositories / DbContext. These build the same registration graph as
/// Program.cs with ValidateOnBuild + ValidateScopes (which fails on a captive
/// dependency) and run the cleanup tick end-to-end to prove the scope-per-run
/// pattern works.
/// </summary>
[TestClass]
public class SchedulerDiLifetimeTests
{
    private static ServiceProvider BuildLikeProgram(SqliteConnection conn)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SuperStatusDb>(o => o.UseSqlite(conn), ServiceLifetime.Scoped);
        services.AddRepositories();   // returns void — can't be chained
        services.AddScoped<IStatusCheckService, StatusCheckService>();
        services.AddSingleton<IHttpClientFactory>(new NoopFactory());
        services.AddSingleton(new SchedulerConcurrencyOptions(8));
        services.AddSingleton(new SchedulerIntervals(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10)));
        // The exact singleton tick + hosted-service registrations Program.cs uses:
        services.AddSingleton<IStatusCheckTick, SuperStatusCheckJob>();
        services.AddSingleton<IDbCleanupTick, SuperStatusCleanUpJob>();
        services.AddHostedService<StatusCheckSchedulerService>();
        services.AddHostedService<DbCleanupSchedulerService>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [TestMethod]
    public void Container_Validates_NoCaptiveScopedDependencyInSingletonTicks()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        try
        {
            // ValidateOnBuild throws here if a singleton (the ticks/hosted
            // services) captures a scoped repository/DbContext.
            using var sp = BuildLikeProgram(conn);
            Assert.IsNotNull(sp.GetRequiredService<IStatusCheckTick>());
            Assert.IsNotNull(sp.GetRequiredService<IDbCleanupTick>());
        }
        finally { conn.Close(); }
    }

    [TestMethod]
    public async Task CleanupTick_OpensItsOwnScope_AndDeletesExpiredRows()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        try
        {
            using var sp = BuildLikeProgram(conn);
            await using (var s = sp.CreateAsyncScope())
            {
                var db = s.ServiceProvider.GetRequiredService<SuperStatusDb>();
                db.Database.EnsureCreated();
                var check = new StatusCheck { Title = "c", StatusCheckUrl = "x", WebHookOnErrorUrl = "y", ServiceLogoUrl = "z" };
                db.StatusCheckSet.Add(check);
                await db.SaveChangesAsync();
                db.HistoricalStatusDataSet.AddRange(
                    new HistoricalStatusData { StatusCheckId = check.Id, TimeOfCheckUTC = DateTime.UtcNow.AddDays(-45), FailType = FailType.NoFail },
                    new HistoricalStatusData { StatusCheckId = check.Id, TimeOfCheckUTC = DateTime.UtcNow.AddDays(-1), FailType = FailType.NoFail });
                await db.SaveChangesAsync();
            }

            // Resolved from root (as the hosted service holds it) — must still
            // work because it opens its own scope internally. #138 (PR-C2): the
            // raw prune is now GATED on a prior backfill marker and uses the
            // ~72 h retention, so the first tick backfills + sets the marker and
            // the second tick prunes. After both, the 45-day row (>72 h) is gone
            // and the 1-day row (<72 h) is kept.
            var tick = sp.GetRequiredService<IDbCleanupTick>();
            await tick.RunCleanupAsync(CancellationToken.None);
            await tick.RunCleanupAsync(CancellationToken.None);

            await using var verify = sp.CreateAsyncScope();
            var vdb = verify.ServiceProvider.GetRequiredService<SuperStatusDb>();
            Assert.AreEqual(1, await vdb.HistoricalStatusDataSet.CountAsync(),
                "the 45-day-old row must be pruned (>72h), the 1-day-old row kept (<72h)");
        }
        finally { conn.Close(); }
    }

    private sealed class NoopFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Noop());
        private sealed class Noop : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
