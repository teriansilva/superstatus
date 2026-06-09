using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Scheduler;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #78 — production-scope regression test. Unlike the fake-service tests,
/// this wires the *real* scoped repositories + StatusCheckService against a
/// SQLite-backed SuperStatusDb, exactly as the job runs in production. It guards
/// the cross-scope EF bug Hermes caught: the job must re-query each check inside
/// its worker scope, so the HistoricalStatusData it produces (and its StatusCheck
/// navigation) is tracked by that scope's DbContext and actually persists.
/// </summary>
[TestClass]
public class SuperStatusCheckJobRelationalTests
{
    private static ServiceProvider BuildProvider(string connString, int maxConcurrency, HttpStatusCode probeStatus)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Each scope opens its OWN connection to the shared-cache in-memory DB
        // (a keep-alive connection holds the DB alive). Sharing a single
        // SqliteConnection across the parallel fan-out scopes is not thread-safe
        // and intermittently throws "database is locked"; per-scope connections
        // to one shared-cache DB behave like Npgsql in production.
        services.AddDbContext<SuperStatusDb>(o => o.UseSqlite(connString), ServiceLifetime.Scoped);
        services.AddRepositories();
        services.AddScoped<IStatusCheckService, StatusCheckService>();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(probeStatus));
        services.AddSingleton(new SchedulerConcurrencyOptions(maxConcurrency));
        return services.BuildServiceProvider();
    }

    private static async Task<long> SeedEnabledCheck(IServiceProvider sp, int expectedStatus = 200)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SuperStatusDb>();
        var check = new StatusCheck
        {
            Title = "probe",
            StatusCheckUrl = "http://probe.test/health",
            ExpectedStatusCode = expectedStatus,
            ExpectedResponseTimeInMs = 60_000,
            Enabled = true,
            IsWebHookOnErrorEnabled = false,
            WebHookOnErrorUrl = string.Empty,
            ServiceLogoUrl = string.Empty,
            Created = DateTime.UtcNow,
        };
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();
        return check.Id;
    }

    [TestMethod]
    public async Task Execute_PersistsHistoricalRow_ThroughProductionScopedServices()
    {
        var connString = $"DataSource=p1test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();   // holds the shared-cache in-memory DB alive for the test
        try
        {
            await using var sp = BuildProvider(connString, maxConcurrency: 4, probeStatus: HttpStatusCode.OK);
            await using (var s = sp.CreateAsyncScope())
                s.ServiceProvider.GetRequiredService<SuperStatusDb>().Database.EnsureCreated();

            long checkId = await SeedEnabledCheck(sp);

            var job = new SuperStatusCheckJob(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<SchedulerConcurrencyOptions>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StatusCheckService>>());

            await job.RunDueChecksAsync();

            await using var verify = sp.CreateAsyncScope();
            var db = verify.ServiceProvider.GetRequiredService<SuperStatusDb>();
            int rows = await db.HistoricalStatusDataSet.CountAsync(h => h.StatusCheckId == checkId);
            Assert.AreEqual(1, rows, "Exactly one historical result must persist for the check.");
            // And no duplicate StatusCheck was inserted by a detached navigation.
            Assert.AreEqual(1, await db.StatusCheckSet.CountAsync(), "The detached navigation must not have created a second StatusCheck.");
        }
        finally
        {
            keepAlive.Close();
        }
    }

    [TestMethod]
    public async Task Execute_ManyChecks_PersistsOneRowEach_UnderFanOut()
    {
        var connString = $"DataSource=p1test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();   // holds the shared-cache in-memory DB alive for the test
        try
        {
            await using var sp = BuildProvider(connString, maxConcurrency: 4, probeStatus: HttpStatusCode.OK);
            await using (var s = sp.CreateAsyncScope())
                s.ServiceProvider.GetRequiredService<SuperStatusDb>().Database.EnsureCreated();

            for (int i = 0; i < 10; i++) await SeedEnabledCheck(sp);

            var job = new SuperStatusCheckJob(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<SchedulerConcurrencyOptions>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StatusCheckService>>());

            await job.RunDueChecksAsync();

            await using var verify = sp.CreateAsyncScope();
            var db = verify.ServiceProvider.GetRequiredService<SuperStatusDb>();
            Assert.AreEqual(10, await db.HistoricalStatusDataSet.CountAsync(),
                "Each of the 10 checks must persist exactly one historical row under bounded fan-out.");
        }
        finally
        {
            keepAlive.Close();
        }
    }

    /// <summary>Factory whose clients always return a fixed status via a stub handler.</summary>
    private sealed class StubHttpClientFactory(HttpStatusCode status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(status));

        private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return new HttpResponseMessage(status);
            }
        }
    }
}
