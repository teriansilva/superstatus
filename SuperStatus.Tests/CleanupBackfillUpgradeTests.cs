using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Scheduler;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #138 (PR-C1) — the upgrade path Hermes flagged on #143. A deployed
/// instance already has rollup rows from PR-A's backfill, written before the
/// <see cref="DailyStatusRollup.Unreachable"/> column existed, so those rows
/// carry Unreachable = 0. The cleanup job's VERSIONED marker must trigger a
/// one-time FULL re-backfill on such an instance (marker 0 &lt; code version),
/// recomputing Unreachable from the still-present raw history — otherwise the
/// migrated detail overview misclassifies historical unreachable samples as
/// bad-status. This test simulates that exact upgrade and proves the correction.
/// </summary>
[TestClass]
public class CleanupBackfillUpgradeTests
{
    private static ServiceProvider BuildProvider(string connString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SuperStatusDb>(o => o.UseSqlite(connString), ServiceLifetime.Scoped);
        services.AddRepositories();
        services.AddScoped<IStatusCheckService, StatusCheckService>();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(HttpStatusCode.OK));
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task Cleanup_OnUpgrade_FullyRebackfillsUnreachable_AndAdvancesMarker()
    {
        var connString = $"DataSource=upgrade-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        try
        {
            await using var sp = BuildProvider(connString);
            await using (var s = sp.CreateAsyncScope())
                s.ServiceProvider.GetRequiredService<SuperStatusDb>().Database.EnsureCreated();

            long checkId;
            var oldDay = DateTime.UtcNow.Date.AddDays(-10);   // within the 30d window, not pruned
            await using (var seed = sp.CreateAsyncScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<SuperStatusDb>();
                var c = new StatusCheck
                {
                    Title = "svc", StatusCheckUrl = "x", WebHookOnErrorUrl = "", ServiceLogoUrl = "",
                    ExpectedStatusCode = 200, ExpectedResponseTimeInMs = 1000, Enabled = true, IntervalSeconds = 30,
                    Created = DateTime.UtcNow.AddDays(-40),
                };
                db.StatusCheckSet.Add(c);
                await db.SaveChangesAsync();
                checkId = c.Id;

                // Raw history for the old day: 3 unreachable + 2 bad-status + 5 healthy.
                for (int i = 0; i < 3; i++) db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = checkId, TimeOfCheckUTC = oldDay.AddMinutes(i), HttpStatusCode = 0, ResponseTimeInMs = 0, CheckFailed = true });
                for (int i = 0; i < 2; i++) db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = checkId, TimeOfCheckUTC = oldDay.AddMinutes(10 + i), HttpStatusCode = 500, ResponseTimeInMs = 80 });
                for (int i = 0; i < 5; i++) db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = checkId, TimeOfCheckUTC = oldDay.AddMinutes(20 + i), HttpStatusCode = 200, ResponseTimeInMs = 100 });

                // PRE-EXISTING rollup row as PR-A would have written it: correct
                // Total/Down, but Unreachable defaulted to 0 (column didn't exist).
                db.DailyStatusRollupSet.Add(new DailyStatusRollup { StatusCheckId = checkId, Day = oldDay, Total = 10, Down = 5, Degraded = 0, Unreachable = 0 });
                // No RollupMaintenanceState row → marker version 0 (pre-marker upgrade).
                await db.SaveChangesAsync();
            }

            // Sanity: the marker starts at 0.
            await using (var pre = sp.CreateAsyncScope())
                Assert.AreEqual(0, await pre.ServiceProvider.GetRequiredService<IDailyStatusRollupRepository>().GetBackfillVersionAsync());

            // Run the cleanup tick exactly as the hosted service would.
            var job = new SuperStatusCleanUpJob(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<StatusCheckService>>());
            await job.RunCleanupAsync();

            await using (var verify = sp.CreateAsyncScope())
            {
                var rollupRepo = verify.ServiceProvider.GetRequiredService<IDailyStatusRollupRepository>();
                var row = (await rollupRepo.GetSinceAsync(checkId, oldDay)).Single(r => r.Day.Date == oldDay);
                Assert.AreEqual(3, row.Unreachable, "full re-backfill recomputed Unreachable from raw on the upgrade path");
                Assert.AreEqual(5, row.Down, "Down stays correct after re-backfill");
                Assert.AreEqual(SuperStatusCleanUpJob.RollupBackfillVersion, await rollupRepo.GetBackfillVersionAsync(), "marker advanced after the full backfill");

                // And the migrated overview now reports the old day's unreachable correctly.
                var svc = verify.ServiceProvider.GetRequiredService<IStatusCheckService>();
                var overview = (await svc.GetHistoricalStatusDataOverviewForRecentTimeRange(checkId, 30))
                    .Single(v => v.Date == DateOnly.FromDateTime(oldDay));
                Assert.AreEqual(3, overview.UnreachableCount, "overview reflects the corrected unreachable count");
                Assert.AreEqual(2, overview.FailedResponseCount, "bad-status = Down − Unreachable");
            }

            // A second tick must NOT re-backfill (marker now current).
            await using (var second = sp.CreateAsyncScope())
            {
                int before = await second.ServiceProvider.GetRequiredService<IDailyStatusRollupRepository>().GetBackfillVersionAsync();
                Assert.AreEqual(SuperStatusCleanUpJob.RollupBackfillVersion, before);
            }
        }
        finally
        {
            keepAlive.Close();
        }
    }

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
