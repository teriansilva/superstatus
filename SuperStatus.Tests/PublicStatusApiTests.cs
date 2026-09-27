using System.Collections.Generic;
using SuperStatus.ApiService;
using SuperStatus.Data.Constants;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #108. The `overall` rule and the FailType → state mapping live
/// in <see cref="PublicStatusApi"/> as pure helpers so they can be
/// reused by the dashboard summary (planned in #104) and unit-tested
/// without spinning the whole web host.
/// </summary>
[TestClass]
public class PublicStatusApiTests
{
    [TestMethod]
    [DataRow(FailType.NoFail,        "up")]
    [DataRow(FailType.ResponseTime,  "degraded")]
    [DataRow(FailType.StatusCode,    "down")]
    [DataRow(FailType.Unreachable,   "down")]
    public void MapStateLabel_MapsKnownFailTypes(FailType f, string expected)
    {
        Assert.AreEqual(expected, PublicStatusApi.MapStateLabel(f));
    }

    [TestMethod]
    public void MapStateLabel_NullFailType_ReturnsUnknown()
    {
        Assert.AreEqual("unknown", PublicStatusApi.MapStateLabel(null));
    }

    [TestMethod]
    public void ComputeOverall_AllUp_NoIncidents_ReturnsUp()
    {
        var services = new[] { Svc("up"), Svc("up") };
        Assert.AreEqual("up", PublicStatusApi.ComputeOverall(services, Array.Empty<PublicStatusOpenIncidentDto>()));
    }

    [TestMethod]
    public void ComputeOverall_OneDegraded_ReturnsDegraded()
    {
        var services = new[] { Svc("up"), Svc("degraded") };
        Assert.AreEqual("degraded", PublicStatusApi.ComputeOverall(services, Array.Empty<PublicStatusOpenIncidentDto>()));
    }

    [TestMethod]
    public void ComputeOverall_OneDown_ReturnsDown_EvenWithIncidentsAndDegraded()
    {
        var services = new[] { Svc("up"), Svc("degraded"), Svc("down") };
        var incidents = new[] { Inc(1, "x") };
        Assert.AreEqual("down", PublicStatusApi.ComputeOverall(services, incidents));
    }

    [TestMethod]
    public void ComputeOverall_AllUpButOpenIncident_ReturnsDegraded()
    {
        var services = new[] { Svc("up"), Svc("up") };
        var incidents = new[] { Inc(1, "elevated latency") };
        Assert.AreEqual("degraded", PublicStatusApi.ComputeOverall(services, incidents));
    }

    [TestMethod]
    public void ComputeOverall_EmptyServices_NoIncidents_ReturnsUp()
    {
        Assert.AreEqual("up", PublicStatusApi.ComputeOverall(
            Array.Empty<PublicStatusServiceDto>(),
            Array.Empty<PublicStatusOpenIncidentDto>()));
    }

    [TestMethod]
    public void Dto_VersionConstantIsOne()
    {
        // Sentinel — bumping this value is a deliberate v2 cut, not an
        // accident. Test fails loudly if it changes.
        Assert.AreEqual("1", PublicStatusApi.ApiVersion);
        Assert.AreEqual("X-SuperStatus-Api-Version", PublicStatusApi.ApiVersionHeader);
    }

    [TestMethod]
    public void CorsPolicyName_IsStableSentinel()
    {
        // Sentinel: renaming the policy name is a runtime configuration
        // change (Program.cs registers it under this exact name).
        Assert.AreEqual("SuperStatusPublicReadOnly", PublicStatusApi.CorsPolicyName);
    }

    [TestMethod]
    public void ResponseDto_HasExpectedV1PropertyNames()
    {
        // Snapshot: rename/remove → breaking change, must go to /api/status/v2.
        var props = typeof(PublicStatusResponseDto).GetProperties()
            .Select(p => GetJsonName(p)).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            new[] { "generated_utc", "incidents_open", "overall", "services" },
            props);

        var svc = typeof(PublicStatusServiceDto).GetProperties()
            .Select(GetJsonName).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            new[] { "expected_response_time_ms", "expected_status_code", "id",
                    "last_checked_utc", "last_latency_ms", "state", "title" },
            svc);

        var inc = typeof(PublicStatusOpenIncidentDto).GetProperties()
            .Select(GetJsonName).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            // #106 PR2 added "severity" — additive within v1 (allowed).
            new[] { "id", "severity", "started_utc", "title" }, inc);
    }

    private static string GetJsonName(System.Reflection.PropertyInfo p)
    {
        var attr = p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
            .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault();
        return attr?.Name ?? p.Name;
    }

    private static PublicStatusServiceDto Svc(string state) =>
        new(Id: 1, Title: "t", State: state,
            LastCheckedUtc: null, LastLatencyMs: null,
            ExpectedStatusCode: 200, ExpectedResponseTimeMs: 500);

    private static PublicStatusOpenIncidentDto Inc(long id, string title) =>
        new(Id: id, Title: title, StartedUtc: DateTime.UtcNow, Severity: "minor");
}
