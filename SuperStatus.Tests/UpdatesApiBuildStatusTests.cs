using SuperStatus.ApiService;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Updates;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #249 (epic #248): the status composition behind GET /api/updates — the
/// comparison verdict from last-known-good state, with the failure surfaced
/// separately and the edge/never-checked cases handled.
/// </summary>
[TestClass]
public class UpdatesApiBuildStatusTests
{
    private static UpdateCheckStateDto State(string? latest, string? error = null, bool enabled = true)
        => new(enabled, DateTime.UtcNow, latest, latest is null ? null : "https://notes", error);

    [TestMethod]
    public void EdgeBuild_isEdgeStatus_noLatestRow()
    {
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("0.0.0-dev", "edge"), State(null), autoUpdateActive: false);
        Assert.AreEqual(UpdateStatusViewModel.StatusEdge, vm.Status);
        Assert.AreEqual("edge", vm.Channel);
    }

    [TestMethod]
    public void NeverChecked_isUnknown()
    {
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State(null), autoUpdateActive: false);
        Assert.AreEqual(UpdateStatusViewModel.StatusUnknown, vm.Status);
    }

    [TestMethod]
    public void NewerLatest_isAvailable_carriesAutoFlag()
    {
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0"), autoUpdateActive: true);
        Assert.AreEqual(UpdateStatusViewModel.StatusUpdateAvailable, vm.Status);
        Assert.AreEqual("1.2.0", vm.LatestVersion);
        Assert.IsTrue(vm.AutoUpdateActive);
    }

    [TestMethod]
    public void SameLatest_isUpToDate()
    {
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.2.0", "latest"), State("1.2.0"), autoUpdateActive: false);
        Assert.AreEqual(UpdateStatusViewModel.StatusUpToDate, vm.Status);
    }

    [TestMethod]
    public void CanApplyInApp_flowsThrough_andDefaultsFalse()
    {
        // Issue #311: the trigger-configured flag rides the view model so the UI can
        // decide whether to offer the "Update now" button. Defaults false (no overlay).
        var off = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0"), autoUpdateActive: false);
        Assert.IsFalse(off.CanApplyInApp);

        var on = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0"), autoUpdateActive: true, canApplyInApp: true);
        Assert.IsTrue(on.CanApplyInApp);
    }

    [TestMethod]
    public void FailedCheck_keepsVerdictFromLastKnown_andSurfacesError()
    {
        // Last successful check saw 1.2.0 (newer than running 1.0.0); the most recent
        // check errored. The verdict stays "available" off the last-known version,
        // and the error is surfaced for the calm "couldn't check" overlay.
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0", error: "boom"), autoUpdateActive: false);
        Assert.AreEqual(UpdateStatusViewModel.StatusUpdateAvailable, vm.Status);
        Assert.AreEqual("boom", vm.LastCheckError);
        Assert.AreEqual("1.2.0", vm.LatestVersion);
    }
}
