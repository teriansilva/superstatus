using SuperStatus.ApiService;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Updates;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #249 (epic #248): the status composition behind GET /api/updates — the
/// comparison verdict from last-known-good state, with the failure surfaced
/// separately and the edge/never-checked cases handled. Issue #334 folds the
/// persisted auto-update policy into the same view model.
/// </summary>
[TestClass]
public class UpdatesApiBuildStatusTests
{
    private static UpdateCheckStateDto State(string? latest, string? error = null, bool enabled = true)
        => new(enabled, DateTime.UtcNow, latest, latest is null ? null : "https://notes", error);

    private static AutoUpdateSettingsDto Auto(bool enabled = false, int hour = 3, int minute = 0, DateTime? lastRun = null)
        => new(enabled, new TimeOnly(hour, minute), lastRun);

    [TestMethod]
    public void EdgeBuild_isEdgeStatus_noLatestRow()
    {
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("0.0.0-dev", "edge"), State(null), Auto());
        Assert.AreEqual(UpdateStatusViewModel.StatusEdge, vm.Status);
        Assert.AreEqual("edge", vm.Channel);
    }

    [TestMethod]
    public void NeverChecked_isUnknown()
    {
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State(null), Auto());
        Assert.AreEqual(UpdateStatusViewModel.StatusUnknown, vm.Status);
    }

    [TestMethod]
    public void NewerLatest_isAvailable()
    {
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0"), Auto());
        Assert.AreEqual(UpdateStatusViewModel.StatusUpdateAvailable, vm.Status);
        Assert.AreEqual("1.2.0", vm.LatestVersion);
    }

    [TestMethod]
    public void SameLatest_isUpToDate()
    {
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.2.0", "latest"), State("1.2.0"), Auto());
        Assert.AreEqual(UpdateStatusViewModel.StatusUpToDate, vm.Status);
    }

    [TestMethod]
    public void AutoUpdatePolicy_flowsThroughFromThePersistedSettings()
    {
        // #334: the toggle + schedule the panel renders come from SiteSettings, not from
        // an env flag and not from Watchtower's (non-existent) schedule.
        var lastRun = new DateTime(2026, 7, 9, 3, 0, 0, DateTimeKind.Utc);
        var vm = UpdatesApi.BuildStatus(
            new AppVersionInfo("1.0.0", "latest"), State("1.2.0"), Auto(enabled: true, hour: 2, minute: 30, lastRun: lastRun));

        Assert.IsTrue(vm.AutoUpdateEnabled);
        Assert.AreEqual(new TimeOnly(2, 30), vm.AutoUpdateTimeUtc);
        Assert.AreEqual(lastRun, vm.AutoUpdateLastRunUtc);
    }

    [TestMethod]
    public void AutoUpdate_defaultsOff_at0300_neverRun()
    {
        // Off by default: the engine ships by default, unattended restarts do not.
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0"), AutoUpdateSettingsDto.Default);

        Assert.IsFalse(vm.AutoUpdateEnabled);
        Assert.AreEqual(new TimeOnly(3, 0), vm.AutoUpdateTimeUtc);
        Assert.IsNull(vm.AutoUpdateLastRunUtc);
    }

    [TestMethod]
    public void CanApplyInApp_flowsThrough_andDefaultsFalse()
    {
        // #311/#334: true whenever the update engine is present (the default install),
        // false on an opted-out install ⇒ the panel falls back to the guided command.
        var off = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0"), Auto());
        Assert.IsFalse(off.CanApplyInApp);

        var on = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0"), Auto(), canApplyInApp: true);
        Assert.IsTrue(on.CanApplyInApp);
    }

    [TestMethod]
    public void FailedCheck_keepsVerdictFromLastKnown_andSurfacesError()
    {
        // Last successful check saw 1.2.0 (newer than running 1.0.0); the most recent
        // check errored. The verdict stays "available" off the last-known version,
        // and the error is surfaced for the calm "couldn't check" overlay.
        var vm = UpdatesApi.BuildStatus(new AppVersionInfo("1.0.0", "latest"), State("1.2.0", error: "boom"), Auto());
        Assert.AreEqual(UpdateStatusViewModel.StatusUpdateAvailable, vm.Status);
        Assert.AreEqual("boom", vm.LastCheckError);
        Assert.AreEqual("1.2.0", vm.LatestVersion);
    }
}
