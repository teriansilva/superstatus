using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Scheduler;
using SuperStatus.Services.Services;
using SuperStatus.Services.Updates;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #334: the daily automatic update. This worker can restart the whole stack,
/// so every guard is tested: the toggle, the opted-out engine, "an update is actually
/// available", the schedule window, and the once-per-slot de-dupe.
///
/// The load-bearing invariant (Hermes' review): <c>AutoUpdateLastRunUtc</c> is written
/// ONLY when the updater accepts the trigger. A transient token/network failure must
/// leave the slot open so a later cycle retries the same day, rather than silently
/// suppressing the update until tomorrow.
/// </summary>
[TestClass]
public class AutoUpdateWorkerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly TimeOnly At0300 = new(3, 0);

    private static DateTime Utc(int day, int hour, int minute = 0)
        => new(2026, 7, day, hour, minute, 0, DateTimeKind.Utc);

    // ── IsDue: the schedule window ────────────────────────────────────────────

    [TestMethod]
    public void NotDue_beforeTheScheduledTime()
    {
        var auto = new AutoUpdateSettingsDto(true, At0300, null);
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 2, 59)));
    }

    [TestMethod]
    public void Due_atTheScheduledTime_andThroughTheCatchUpWindow()
    {
        var auto = new AutoUpdateSettingsDto(true, At0300, null);
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 3, 0)), "exactly on time");
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 3, 59)), "a late cycle inside the window still applies");
    }

    [TestMethod]
    public void NotDue_onceTheWindowHasClosed()
    {
        // Switching auto-update ON at 10:00 with a 03:00 schedule must NOT apply an
        // update immediately — the console promises "tonight at 03:00 UTC".
        var auto = new AutoUpdateSettingsDto(true, At0300, null);
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 4, 0)), "window closed");
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 10, 0)), "no retroactive mid-day restart");
    }

    [TestMethod]
    public void NotDue_whenTodaysSlotAlreadyRan()
    {
        var auto = new AutoUpdateSettingsDto(true, At0300, LastRunUtc: Utc(10, 3, 1));
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 3, 30)), "one accepted run per slot");
    }

    [TestMethod]
    public void Due_againTheNextDay_afterYesterdaysRun()
    {
        var auto = new AutoUpdateSettingsDto(true, At0300, LastRunUtc: Utc(10, 3, 1));
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 3, 0)));
    }

    [TestMethod]
    public void Due_afterAFailedAttemptEarlierInTheSameSlot()
    {
        // The failed attempt left LastRunUtc unset (see the accept-only test below), so
        // the next cycle inside the window retries.
        var auto = new AutoUpdateSettingsDto(true, At0300, LastRunUtc: null);
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 3, 5)));
    }

    // ── Near-midnight schedules: the slot's window crosses into the next day ──

    private static readonly TimeOnly At2330 = new(23, 30);

    [TestMethod]
    public void Due_afterMidnight_whenYesterdaysLateSlotIsStillOpen()
    {
        // Regression (Hermes, PR #379): a 23:30 schedule with a 1h window is still due at
        // 00:15 the next day — the slot that opened at 23:30 closes at 00:30. Considering
        // only *today's* slot silently skipped this operator's update every single night.
        var auto = new AutoUpdateSettingsDto(true, At2330, LastRunUtc: null);
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 0, 15)));
    }

    [TestMethod]
    public void Due_atALateScheduledTime_beforeMidnight()
    {
        var auto = new AutoUpdateSettingsDto(true, At2330, LastRunUtc: null);
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 23, 30)));
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(10, 23, 29)), "not before the slot opens");
    }

    [TestMethod]
    public void NotDue_afterMidnight_onceYesterdaysLateSlotHasClosed()
    {
        var auto = new AutoUpdateSettingsDto(true, At2330, LastRunUtc: null);
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 0, 30)), "slot closed exactly at +1h");
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 2, 0)), "long past the window");
    }

    [TestMethod]
    public void NotDue_afterMidnight_whenYesterdaysLateSlotAlreadyRan()
    {
        // Accepted at 23:35; the same slot must not fire again after midnight.
        var auto = new AutoUpdateSettingsDto(true, At2330, LastRunUtc: Utc(10, 23, 35));
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 0, 15)));
    }

    [TestMethod]
    public void Due_afterMidnight_whenYesterdaysLateSlotFailedTransiently()
    {
        // A failed attempt at 23:35 leaves LastRunUtc unset (it is stamped only on
        // Accepted), so a cycle at 00:15 still retries the same slot.
        var auto = new AutoUpdateSettingsDto(true, At2330, LastRunUtc: null);
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 0, 15)));

        // ...but a run accepted two days ago doesn't consume last night's slot.
        var stale = new AutoUpdateSettingsDto(true, At2330, LastRunUtc: Utc(9, 23, 40));
        Assert.IsTrue(AutoUpdateWorker.IsDue(stale, Window, Utc(11, 0, 15)));
    }

    [TestMethod]
    public void MidnightSchedule_isDueAtMidnight_andNotAnHourLater()
    {
        var auto = new AutoUpdateSettingsDto(true, new TimeOnly(0, 0), LastRunUtc: null);
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 0, 0)));
        Assert.IsTrue(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 0, 59)));
        Assert.IsFalse(AutoUpdateWorker.IsDue(auto, Window, Utc(11, 1, 0)));
    }

    // ── RunCycleAsync: the guards, and the accept-only stamp ──────────────────

    [TestMethod]
    public async Task Cycle_doesNothing_whenAutoUpdateIsOff()
    {
        var settings = new FakeSettings(new AutoUpdateSettingsDto(false, At0300, null), Available);
        var trigger = new FakeTrigger(canApply: true, UpdateTriggerOutcome.Accepted);

        await RunCycle(settings, trigger, Utc(10, 3, 0));

        Assert.AreEqual(0, trigger.Calls, "the toggle is the only thing that can start an unattended update");
        Assert.IsNull(settings.MarkedRunUtc);
    }

    [TestMethod]
    public async Task Cycle_doesNothing_whenTheEngineIsOptedOut()
    {
        // --no-updater ⇒ no trigger URL/token ⇒ CanApply=false. The toggle may still be
        // on from before the operator switched engines; that must not throw or fire.
        var settings = new FakeSettings(new AutoUpdateSettingsDto(true, At0300, null), Available);
        var trigger = new FakeTrigger(canApply: false, UpdateTriggerOutcome.NotConfigured);

        await RunCycle(settings, trigger, Utc(10, 3, 0));

        Assert.AreEqual(0, trigger.Calls);
        Assert.IsNull(settings.MarkedRunUtc);
    }

    [TestMethod]
    public async Task Cycle_doesNothing_whenNoUpdateIsAvailable()
    {
        var settings = new FakeSettings(new AutoUpdateSettingsDto(true, At0300, null), UpToDate);
        var trigger = new FakeTrigger(canApply: true, UpdateTriggerOutcome.Accepted);

        await RunCycle(settings, trigger, Utc(10, 3, 0));

        Assert.AreEqual(0, trigger.Calls, "never restart the stack when there is nothing to apply");
        Assert.IsNull(settings.MarkedRunUtc);
    }

    [TestMethod]
    public async Task Cycle_triggersAndStampsLastRun_whenAccepted()
    {
        var settings = new FakeSettings(new AutoUpdateSettingsDto(true, At0300, null), Available);
        var trigger = new FakeTrigger(canApply: true, UpdateTriggerOutcome.Accepted);
        var now = Utc(10, 3, 0);

        await RunCycle(settings, trigger, now);

        Assert.AreEqual(1, trigger.Calls);
        Assert.AreEqual(now, settings.MarkedRunUtc, "the slot is consumed once the updater owns the work");
    }

    [TestMethod]
    public async Task Cycle_doesNotStampLastRun_whenTheTriggerIsRejected()
    {
        // THE regression Hermes asked for: a bad token must not burn the day's slot.
        var settings = new FakeSettings(new AutoUpdateSettingsDto(true, At0300, null), Available);
        var trigger = new FakeTrigger(canApply: true, UpdateTriggerOutcome.Unauthorized);

        await RunCycle(settings, trigger, Utc(10, 3, 0));

        Assert.AreEqual(1, trigger.Calls, "it tried");
        Assert.IsNull(settings.MarkedRunUtc, "a rejected trigger must leave the slot open for a retry");
    }

    [TestMethod]
    public async Task Cycle_doesNotStampLastRun_whenTheUpdaterIsUnreachable()
    {
        var settings = new FakeSettings(new AutoUpdateSettingsDto(true, At0300, null), Available);
        var trigger = new FakeTrigger(canApply: true, UpdateTriggerOutcome.Unreachable);

        await RunCycle(settings, trigger, Utc(10, 3, 0));

        Assert.IsNull(settings.MarkedRunUtc);
    }

    [TestMethod]
    public async Task Cycle_retriesInTheSameSlot_afterATransientFailure_thenStampsOnAccept()
    {
        // Cycle 1 at 03:00 fails transiently; cycle 2 at 03:05 succeeds — same day.
        var settings = new FakeSettings(new AutoUpdateSettingsDto(true, At0300, null), Available);
        var trigger = new FakeTrigger(canApply: true, UpdateTriggerOutcome.Unreachable);

        await RunCycle(settings, trigger, Utc(10, 3, 0));
        Assert.IsNull(settings.MarkedRunUtc);

        trigger.Outcome = UpdateTriggerOutcome.Accepted;
        await RunCycle(settings, trigger, Utc(10, 3, 5));

        Assert.AreEqual(2, trigger.Calls);
        Assert.AreEqual(Utc(10, 3, 5), settings.MarkedRunUtc, "the retry landed on the same day");
    }

    [TestMethod]
    public async Task Cycle_doesNotFireTwice_onceTheSlotIsConsumed()
    {
        var settings = new FakeSettings(new AutoUpdateSettingsDto(true, At0300, null), Available);
        var trigger = new FakeTrigger(canApply: true, UpdateTriggerOutcome.Accepted);

        await RunCycle(settings, trigger, Utc(10, 3, 0));
        await RunCycle(settings, trigger, Utc(10, 3, 30));   // a later cycle in the same window

        Assert.AreEqual(1, trigger.Calls, "one accepted apply per slot");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static readonly UpdateCheckStateDto Available =
        new(Enabled: true, DateTime.UtcNow, LatestVersion: "1.3.3", LatestNotesUrl: null, LastCheckError: null);

    private static readonly UpdateCheckStateDto UpToDate =
        new(Enabled: true, DateTime.UtcNow, LatestVersion: "1.3.2", LatestNotesUrl: null, LastCheckError: null);

    private static Task RunCycle(FakeSettings settings, FakeTrigger trigger, DateTime nowUtc)
    {
        var services = new ServiceCollection();
        services.AddScoped<ISiteSettingsService>(_ => settings);
        services.AddScoped<IUpdateTrigger>(_ => trigger);
        services.AddScoped<IAppVersionProvider>(_ => new FakeVersion("1.3.2", "latest"));
        var provider = services.BuildServiceProvider();

        return AutoUpdateWorker.RunCycleAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AutoUpdateOptions(TimeSpan.FromMinutes(5), Window),
            nowUtc,
            NullLogger.Instance,
            CancellationToken.None);
    }

    private sealed class FakeVersion(string version, string channel) : IAppVersionProvider
    {
        public AppVersionInfo Current { get; } = new(version, channel);
    }

    private sealed class FakeTrigger(bool canApply, UpdateTriggerOutcome outcome) : IUpdateTrigger
    {
        public UpdateTriggerOutcome Outcome { get; set; } = outcome;
        public int Calls { get; private set; }
        public bool CanApply => canApply;

        public Task<UpdateTriggerResult> TriggerAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new UpdateTriggerResult(Outcome, Outcome == UpdateTriggerOutcome.Accepted ? null : "nope"));
        }
    }

    /// <summary>Only the members AutoUpdateWorker touches; the rest throw so an
    /// accidental new dependency shows up as a failing test rather than a silent no-op.</summary>
    private sealed class FakeSettings(AutoUpdateSettingsDto auto, UpdateCheckStateDto state) : ISiteSettingsService
    {
        private AutoUpdateSettingsDto _auto = auto;
        public DateTime? MarkedRunUtc { get; private set; }

        public Task<AutoUpdateSettingsDto> GetAutoUpdateSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_auto);

        public Task<UpdateCheckStateDto> GetUpdateCheckStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(state);

        public Task MarkAutoUpdateRunAsync(DateTime whenUtc, CancellationToken cancellationToken = default)
        {
            MarkedRunUtc = whenUtc;
            _auto = _auto with { LastRunUtc = whenUtc };
            return Task.CompletedTask;
        }

        public Task<AutoUpdateSettingsDto> SetAutoUpdateSettingsAsync(bool enabled, TimeOnly timeUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<SuperStatus.Data.ViewModels.SiteSettingsViewModel> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<SuperStatus.Data.ViewModels.SiteSettingsViewModel> SaveSettingsAsync(SuperStatus.Data.ViewModels.SiteSettingsViewModel settings, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<SuperStatus.Data.ViewModels.SiteSettingsViewModel> CompleteOnboardingAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<SuperStatus.Data.ViewModels.SiteSettingsViewModel> SaveSmtpSettingsAsync(SuperStatus.Data.ViewModels.SiteSettingsViewModel settings, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<SuperStatus.Data.ViewModels.SiteSettingsViewModel> SaveAuthHostsAsync(SuperStatus.Data.ViewModels.SiteSettingsViewModel settings, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SetUpdateCheckResultAsync(string? latestVersion, string? latestNotesUrl, string? error, DateTime checkedUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<string> GetOrCreateVapidPublicKeyAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
