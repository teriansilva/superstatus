using SuperStatus.Services.Services;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #104. Pure-function aggregation helpers on StatusCheckService.
/// The full Postgres-backed aggregation is covered by integration runs
/// against staging; these tests pin the helper math.
/// </summary>
[TestClass]
public class DashboardSummaryTests
{
    [TestMethod]
    [DataRow(0, 0, 0, 0, "up")]                  // empty / no checks
    [DataRow(15, 0, 0, 0, "up")]                 // all up, no incidents
    [DataRow(14, 1, 0, 0, "degraded")]           // any degraded → degraded
    [DataRow(15, 0, 0, 3, "degraded")]           // any open incident → degraded
    [DataRow(14, 1, 0, 3, "degraded")]
    [DataRow(0, 0, 1, 0, "down")]                // any down → down trumps incidents
    [DataRow(14, 0, 1, 9, "down")]
    public void ComputeOverall_AppliesPrecedence_DownBeatsDegradedBeatsUp(int up, int degraded, int down, int incidents, string expected)
    {
        Assert.AreEqual(expected, StatusCheckService.ComputeOverall(up, degraded, down, incidents));
    }

    [TestMethod]
    public void MostRecentState_NullSampleIsUnknown()
    {
        var check = new StatusCheck { ExpectedStatusCode = 200, Sla = SlaTestUtil.Mirror(500) };
        Assert.AreEqual("unknown", StatusCheckService.MostRecentState(check, null));
    }

    [TestMethod]
    [DataRow(FailType.Unreachable, "down")]   // transport fail
    [DataRow(FailType.StatusCode, "down")]    // wrong status / bad response
    [DataRow(FailType.ResponseTime, "degraded")] // slow / below threshold
    [DataRow(FailType.NoFail, "up")]
    public void MostRecentState_MapsStoredFailType(FailType failType, string expected)
    {
        // #317: the state derives from the stored FailType the provider classified
        // (ExecuteStatusCheck sets it from the probe + SLA), so MostRecentState just maps
        // it — provider-agnostic and correct for non-HTTP providers. The FailType
        // classification itself (status/slow/transport) is covered by the provider/parity
        // tests (CheckProviderTests, StatusCheckHttpClientTests).
        var check = new StatusCheck { ExpectedStatusCode = 200, Sla = SlaTestUtil.Mirror(500) };
        var tick = new HistoricalStatusData { FailType = failType };
        Assert.AreEqual(expected, StatusCheckService.MostRecentState(check, tick));
    }

    [TestMethod]
    public void Percentile_EmptyReturnsZero()
    {
        Assert.AreEqual(0, StatusCheckService.Percentile(Array.Empty<int>(), 0.95));
    }

    [TestMethod]
    public void Percentile_NearestRank()
    {
        // 100 samples 1..100. p95 nearest-rank → ceil(0.95 * 100) = 95 → idx 94 → value 95.
        var samples = Enumerable.Range(1, 100).ToList();
        Assert.AreEqual(95, StatusCheckService.Percentile(samples, 0.95));
    }

    [TestMethod]
    public void Percentile_HandlesSingleSample()
    {
        Assert.AreEqual(42, StatusCheckService.Percentile(new[] { 42 }, 0.95));
    }
}
