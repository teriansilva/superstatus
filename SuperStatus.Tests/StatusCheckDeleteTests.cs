using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;
using SuperStatus.Web;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #164 — status-check delete. Proves the relational cascade (children
/// are removed with the check), the service's found/not-found result, and the
/// client's 204→true / 404→false mapping.
/// </summary>
[TestClass]
public class StatusCheckDeleteTests
{
    private static StatusCheckService Svc(SuperStatusDb db) =>
        new(new StatusCheckRepository(db), new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db), new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            new StubFactory(), NullLogger<StatusCheckService>.Instance);

    [TestMethod]
    public async Task Delete_RemovesCheck_AndCascadesChildren()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var check = new StatusCheck
        {
            Title = "to delete", StatusCheckUrl = "http://x/health",
            WebHookOnErrorUrl = "", ServiceLogoUrl = "",
            ExpectedStatusCode = 200, ExpectedResponseTimeInMs = 3000, Enabled = true,
            Created = DateTime.UtcNow,
        };
        db.StatusCheckSet.Add(check);
        await db.SaveChangesAsync();
        long id = check.Id;

        // One of each check-owned child row.
        var hist = new HistoricalStatusData(new StatusCheckResult(check, 0, 0, true), FailType.Unreachable) { StatusCheckId = id };
        db.HistoricalStatusDataSet.Add(hist);
        db.DailyStatusRollupSet.Add(new DailyStatusRollup { StatusCheckId = id, Day = DateTime.UtcNow.Date, Total = 10, Down = 1, Degraded = 0, Unreachable = 0 });
        db.WebhookExecutionLogSet.Add(new WebhookExecutionLog { StatusCheckId = id, AttemptedUtc = DateTime.UtcNow, TargetUrl = "http://hook", HttpStatusCode = 200, ResponseTimeMs = 10 });
        await db.SaveChangesAsync();

        bool ok = await Svc(db).DeleteStatusCheckAsync(id);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, await db.StatusCheckSet.CountAsync(), "the check is gone");
        Assert.AreEqual(0, await db.HistoricalStatusDataSet.CountAsync(), "historical ticks cascade");
        Assert.AreEqual(0, await db.DailyStatusRollupSet.CountAsync(), "daily rollups cascade");
        Assert.AreEqual(0, await db.WebhookExecutionLogSet.CountAsync(), "webhook logs cascade");
    }

    [TestMethod]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        Assert.IsFalse(await Svc(db).DeleteStatusCheckAsync(999), "no such check → false (→ 404 at the API)");
    }

    [TestMethod]
    public async Task Client_Delete_Maps204ToTrue_And404ToFalse()
    {
        var ok = new StatusApiClient(new HttpClient(new CodeStub(HttpStatusCode.NoContent)) { BaseAddress = new Uri("http://api.test") });
        Assert.IsTrue(await ok.DeleteStatusCheckAsync(1));

        var missing = new StatusApiClient(new HttpClient(new CodeStub(HttpStatusCode.NotFound)) { BaseAddress = new Uri("http://api.test") });
        Assert.IsFalse(await missing.DeleteStatusCheckAsync(2));
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new CodeStub(HttpStatusCode.OK));
    }

    private sealed class CodeStub : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        public CodeStub(HttpStatusCode code) { _code = code; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent("", Encoding.UTF8, "application/json") });
    }
}
