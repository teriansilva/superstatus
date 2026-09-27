using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Scheduler;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #84 — the PeriodicTimer-based hosted services that replaced Quartz.
/// Quartz's [DisallowConcurrentExecution] across-tick guard is gone, so these
/// pin the replacement guarantees: ticks never overlap (each is awaited before
/// the next), a tick that throws doesn't kill the loop, and shutdown cancels
/// cleanly mid-tick.
/// </summary>
[TestClass]
public class SchedulerHostedServiceTests
{
    [TestMethod]
    public async Task StatusScheduler_NeverOverlaps_EvenWhenTickIsSlowerThanInterval()
    {
        // Tick takes ~80ms; interval is 20ms. If ticks could overlap, observed
        // concurrency would exceed 1. The do-while-await structure must keep it
        // at exactly 1.
        var tick = new RecordingTick(workMs: 80);
        var svc = new StatusCheckSchedulerService(tick,
            new SchedulerIntervals(TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(1)),
            NullLogger<StatusCheckSchedulerService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        await Task.Delay(350);
        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, tick.MaxObservedConcurrency, "ticks must never overlap");
        Assert.IsTrue(tick.Runs >= 2, $"expected several sequential ticks, saw {tick.Runs}");
    }

    [TestMethod]
    public async Task StatusScheduler_RunsAtStartup_BeforeFirstInterval()
    {
        // A long interval — if the first run waited a full interval the test
        // would time out. The do-while runs once immediately.
        var tick = new RecordingTick(workMs: 0);
        var svc = new StatusCheckSchedulerService(tick,
            new SchedulerIntervals(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)),
            NullLogger<StatusCheckSchedulerService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await svc.StopAsync(CancellationToken.None);

        Assert.IsTrue(tick.Runs >= 1, "must run once at startup, not wait a full interval");
    }

    [TestMethod]
    public async Task StatusScheduler_TickThrows_LoopKeepsGoing()
    {
        var tick = new RecordingTick(workMs: 0) { ThrowEachRun = true };
        var svc = new StatusCheckSchedulerService(tick,
            new SchedulerIntervals(TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(1)),
            NullLogger<StatusCheckSchedulerService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await svc.StopAsync(CancellationToken.None);

        Assert.IsTrue(tick.Runs >= 2, "a throwing tick must not stop the scheduler");
    }

    [TestMethod]
    public async Task StatusScheduler_GracefulShutdown_NoErrorAndStops()
    {
        var tick = new RecordingTick(workMs: 0);
        var svc = new StatusCheckSchedulerService(tick,
            new SchedulerIntervals(TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(1)),
            NullLogger<StatusCheckSchedulerService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(80);
        // StopAsync should complete promptly without throwing (clean cancel).
        var stop = svc.StopAsync(CancellationToken.None);
        var done = await Task.WhenAny(stop, Task.Delay(2000));
        Assert.AreSame(stop, done, "graceful shutdown must complete promptly");
        Assert.IsTrue(stop.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task CleanupScheduler_RunsAndNeverOverlaps()
    {
        var tick = new RecordingCleanup(workMs: 60);
        var svc = new DbCleanupSchedulerService(tick,
            new SchedulerIntervals(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20)),
            NullLogger<DbCleanupSchedulerService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, tick.MaxObservedConcurrency, "cleanup must never overlap itself");
        Assert.IsTrue(tick.Runs >= 2);
    }

    private sealed class RecordingTick(int workMs) : IStatusCheckTick
    {
        private int _inFlight, _max, _runs;
        public bool ThrowEachRun { get; init; }
        public int MaxObservedConcurrency => Volatile.Read(ref _max);
        public int Runs => Volatile.Read(ref _runs);

        public async Task RunDueChecksAsync(CancellationToken ct = default)
        {
            int now = Interlocked.Increment(ref _inFlight);
            int obs; while (now > (obs = Volatile.Read(ref _max))) Interlocked.CompareExchange(ref _max, now, obs);
            try
            {
                Interlocked.Increment(ref _runs);
                if (ThrowEachRun) throw new InvalidOperationException("boom");
                if (workMs > 0) await Task.Delay(workMs, ct);
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private sealed class RecordingCleanup(int workMs) : IDbCleanupTick
    {
        private int _inFlight, _max, _runs;
        public int MaxObservedConcurrency => Volatile.Read(ref _max);
        public int Runs => Volatile.Read(ref _runs);

        public async Task RunCleanupAsync(CancellationToken ct = default)
        {
            int now = Interlocked.Increment(ref _inFlight);
            int obs; while (now > (obs = Volatile.Read(ref _max))) Interlocked.CompareExchange(ref _max, now, obs);
            try { Interlocked.Increment(ref _runs); if (workMs > 0) await Task.Delay(workMs, ct); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }
}
