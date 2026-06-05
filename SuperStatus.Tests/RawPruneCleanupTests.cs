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
/// Issue #138 (PR-C2) — the small-footprint raw-tick prune. Proves the gating
/// invariant (raw is never pruned before its rollups exist), the rollup-then-
/// prune ordering that preserves history across the prune, and the bounded
/// rollup table.
/// </summary>
[TestClass]
public class RawPruneCleanupTests
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

    private static SuperStatusCleanUpJob Job(ServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<StatusCheckService>>());

    private static async Task<long> SeedCheck(ServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SuperStatusDb>();
        var c = new StatusCheck
        {
            Title = "svc", StatusCheckUrl = "x", WebHookOnErrorUrl = "", ServiceLogoUrl = "",
            ExpectedStatusCode = 200, ExpectedResponseTimeInMs = 1000, Enabled = true, IntervalSeconds = 30,
            Created = DateTime.UtcNow.AddDays(-40),
        };
        db.StatusCheckSet.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    [TestMethod]
    public async Task Cleanup_BackfillsThenPrunes_PreservingRollupsAcrossThePrune()
    {
        var connString = $"DataSource=prune-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connString); keepAlive.Open();
        try
        {
            await using var sp = BuildProvider(connString);
            await using (var s = sp.CreateAsyncScope())
                s.ServiceProvider.GetRequiredService<SuperStatusDb>().Database.EnsureCreated();

            long id = await SeedCheck(sp);
            var oldDay = DateTime.UtcNow.Date.AddDays(-10);   // >72h old, within the 30d window
            var recent = DateTime.UtcNow.AddHours(-1);        // inside the 72h window
            await using (var seed = sp.CreateAsyncScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<SuperStatusDb>();
                for (int i = 0; i < 5; i++) db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = id, TimeOfCheckUTC = oldDay.AddMinutes(i), HttpStatusCode = 200, ResponseTimeInMs = 100 });
                for (int i = 0; i < 3; i++) db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = id, TimeOfCheckUTC = recent.AddMinutes(i), HttpStatusCode = 200, ResponseTimeInMs = 100 });
                await db.SaveChangesAsync();
            }

            // TICK 1 — fresh instance (marker 0): full backfill + set marker, but
            // raw MUST NOT be pruned yet (prune is gated on a prior marker).
            await Job(sp).RunCleanupAsync();
            await using (var v1 = sp.CreateAsyncScope())
            {
                var db = v1.ServiceProvider.GetRequiredService<SuperStatusDb>();
                Assert.AreEqual(8, await db.HistoricalStatusDataSet.CountAsync(), "tick 1 defers the prune — all raw retained");
                Assert.AreEqual(1, await v1.ServiceProvider.GetRequiredService<IDailyStatusRollupRepository>().GetBackfillVersionAsync(), "marker advanced");
                Assert.IsTrue(await db.DailyStatusRollupSet.AnyAsync(r => r.Day == oldDay), "old day was rolled up before any prune");
            }

            // TICK 2 — marker now set: raw older than retention is pruned, recent kept,
            // and the old day's ROLLUP survives (history preserved across the prune).
            await Job(sp).RunCleanupAsync();
            await using (var v2 = sp.CreateAsyncScope())
            {
                var db = v2.ServiceProvider.GetRequiredService<SuperStatusDb>();
                Assert.AreEqual(3, await db.HistoricalStatusDataSet.CountAsync(), "tick 2 pruned the >72h raw, kept the recent ticks");
                Assert.IsFalse(await db.HistoricalStatusDataSet.AnyAsync(h => h.TimeOfCheckUTC < DateTime.UtcNow.AddDays(-1)), "no raw older than the retention window remains");
                Assert.IsTrue(await db.DailyStatusRollupSet.AnyAsync(r => r.Day == oldDay), "the old day's rollup survives the raw prune");
            }
        }
        finally { keepAlive.Close(); }
    }

    [TestMethod]
    public async Task Cleanup_BoundsRollupTable_BeyondGraphWindow()
    {
        var connString = $"DataSource=prune-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connString); keepAlive.Open();
        try
        {
            await using var sp = BuildProvider(connString);
            await using (var s = sp.CreateAsyncScope())
                s.ServiceProvider.GetRequiredService<SuperStatusDb>().Database.EnsureCreated();

            long id = await SeedCheck(sp);
            await using (var seed = sp.CreateAsyncScope())
            {
                var rollupRepo = seed.ServiceProvider.GetRequiredService<IDailyStatusRollupRepository>();
                await rollupRepo.UpsertAsync(id, DateTime.UtcNow.Date.AddDays(-40), total: 10, down: 0, degraded: 0);   // beyond 30d
                await rollupRepo.UpsertAsync(id, DateTime.UtcNow.Date.AddDays(-5), total: 10, down: 0, degraded: 0);    // within 30d
            }

            await Job(sp).RunCleanupAsync();

            await using (var v = sp.CreateAsyncScope())
            {
                var db = v.ServiceProvider.GetRequiredService<SuperStatusDb>();
                Assert.IsFalse(await db.DailyStatusRollupSet.AnyAsync(r => r.Day == DateTime.UtcNow.Date.AddDays(-40)), "rollup beyond the 30d window is pruned");
                Assert.IsTrue(await db.DailyStatusRollupSet.AnyAsync(r => r.Day == DateTime.UtcNow.Date.AddDays(-5)), "rollup within the window is kept");
            }
        }
        finally { keepAlive.Close(); }
    }

    [TestMethod]
    public async Task BulkDeleteRawOlderThanHours_DeletesOldKeepsRecent()
    {
        var connString = $"DataSource=prune-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connString); keepAlive.Open();
        try
        {
            await using var sp = BuildProvider(connString);
            await using (var s = sp.CreateAsyncScope())
                s.ServiceProvider.GetRequiredService<SuperStatusDb>().Database.EnsureCreated();
            long id = await SeedCheck(sp);
            await using (var seed = sp.CreateAsyncScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<SuperStatusDb>();
                db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = id, TimeOfCheckUTC = DateTime.UtcNow.AddHours(-100), HttpStatusCode = 200, ResponseTimeInMs = 100 });
                db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = id, TimeOfCheckUTC = DateTime.UtcNow.AddHours(-10), HttpStatusCode = 200, ResponseTimeInMs = 100 });
                await db.SaveChangesAsync();
            }
            await using (var act = sp.CreateAsyncScope())
            {
                var repo = act.ServiceProvider.GetRequiredService<IHistoricalStatusDataRepository>();
                int deleted = await repo.BulkDeleteRawOlderThanHoursAsync(72);
                Assert.AreEqual(1, deleted, "only the >72h row is deleted");
            }
            await using (var v = sp.CreateAsyncScope())
            {
                var db = v.ServiceProvider.GetRequiredService<SuperStatusDb>();
                Assert.AreEqual(1, await db.HistoricalStatusDataSet.CountAsync());
                Assert.IsTrue(await db.HistoricalStatusDataSet.AllAsync(h => h.TimeOfCheckUTC > DateTime.UtcNow.AddHours(-72)));
            }
        }
        finally { keepAlive.Close(); }
    }

    [TestMethod]
    public async Task BulkDeleteRaw_Batches_ClearsBacklogLargerThanBatch()
    {
        // Regression for the staging command-timeout: a backlog larger than one
        // batch must be fully cleared across multiple bounded rounds.
        var connString = $"DataSource=prune-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connString); keepAlive.Open();
        try
        {
            await using var sp = BuildProvider(connString);
            await using (var s = sp.CreateAsyncScope())
                s.ServiceProvider.GetRequiredService<SuperStatusDb>().Database.EnsureCreated();
            long id = await SeedCheck(sp);
            await using (var seed = sp.CreateAsyncScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<SuperStatusDb>();
                for (int i = 0; i < 5; i++) db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = id, TimeOfCheckUTC = DateTime.UtcNow.AddHours(-100 - i), HttpStatusCode = 200, ResponseTimeInMs = 100 });
                for (int i = 0; i < 2; i++) db.HistoricalStatusDataSet.Add(new HistoricalStatusData { StatusCheckId = id, TimeOfCheckUTC = DateTime.UtcNow.AddHours(-10 - i), HttpStatusCode = 200, ResponseTimeInMs = 100 });
                await db.SaveChangesAsync();
            }
            await using (var act = sp.CreateAsyncScope())
            {
                var repo = act.ServiceProvider.GetRequiredService<IHistoricalStatusDataRepository>();
                int deleted = await repo.BulkDeleteRawOlderThanHoursAsync(72, batchSize: 2);   // 5 old → 3 rounds (2+2+1)
                Assert.AreEqual(5, deleted, "all 5 expired rows removed across batches");
            }
            await using (var v = sp.CreateAsyncScope())
            {
                var db = v.ServiceProvider.GetRequiredService<SuperStatusDb>();
                Assert.AreEqual(2, await db.HistoricalStatusDataSet.CountAsync(), "the 2 recent rows are kept");
            }
        }
        finally { keepAlive.Close(); }
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
