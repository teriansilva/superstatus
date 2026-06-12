using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.ViewModels;
using SuperStatus.Scheduler;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

[TestClass]
public class SuperStatusCheckJobTests
{
    /// <summary>
    /// Build the job with a real DI scope factory whose scopes all resolve the
    /// same fake service (so its recorded state is observable), mirroring the
    /// production <c>IServiceScopeFactory</c> path (issue #78).
    /// </summary>
    private static SuperStatusCheckJob Build(FakeStatusCheckService fake, int maxConcurrency = 8)
        => Build(fake, new NoopAlertEvaluator(), maxConcurrency);

    private static SuperStatusCheckJob Build(FakeStatusCheckService fake, SuperStatus.Services.Alerts.IAlertEvaluator alertEvaluator, int maxConcurrency = 8)
    {
        var sp = new ServiceCollection()
            .AddScoped<IStatusCheckService>(_ => fake)
            // #168: the job resolves the coordinator after recording each outcome.
            // These tests don't exercise auto-incidents → a no-op stub.
            .AddScoped<IAutoIncidentCoordinator>(_ => new NoopAutoIncidentCoordinator())
            // #241/#253: the job resolves the alert evaluator after the outcome update.
            .AddScoped(_ => alertEvaluator)
            .BuildServiceProvider();
        return new SuperStatusCheckJob(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new SchedulerConcurrencyOptions(maxConcurrency),
            NullLogger<StatusCheckService>.Instance);
    }

    [TestMethod]
    public async Task Execute_AlertEvaluator_RunsPerTick_EvenWhenWebhookPostTaskThrows()
    {
        // #241/#253: the alert evaluator runs immediately after the failure-state
        // update, BEFORE the webhook post-tasks — so a throwing webhook step can't
        // skip alert evaluation for that tick.
        var checks = new[]
        {
            new StatusCheck { Id = 1, Title = "alpha", Enabled = true },
            new StatusCheck { Id = 2, Title = "bravo-webhook-explodes", Enabled = true },
            new StatusCheck { Id = 3, Title = "charlie", Enabled = true },
        };
        var fake = new FakeStatusCheckService(checks) { ThrowOnPostTaskForCheckId = 2 };
        var recorder = new RecordingAlertEvaluator();

        await Build(fake, recorder).RunDueChecksAsync();

        // Evaluator saw every check — including #2, whose webhook post-task threw.
        CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, recorder.EvaluatedCheckIds.ToArray(),
            "alert evaluation runs before the webhook post-tasks, so a webhook failure can't skip it");
    }

    [TestMethod]
    public async Task Execute_OneFailingCheck_DoesNotAbortRemainingChecks()
    {
        // Issue #76: a thrown exception on one check must not abort the rest of
        // the tick. Under #78's fan-out the per-iteration try/catch keeps
        // Parallel.ForEachAsync from cancelling siblings.
        var checks = new[]
        {
            new StatusCheck { Id = 1, Title = "alpha", Enabled = true },
            new StatusCheck { Id = 2, Title = "bravo-explodes", Enabled = true },
            new StatusCheck { Id = 3, Title = "charlie", Enabled = true },
        };
        var fake = new FakeStatusCheckService(checks, throwOnSaveForCheckId: 2);

        await Build(fake).RunDueChecksAsync();

        CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, fake.ExecutedCheckIds.ToArray());
        CollectionAssert.AreEquivalent(new long[] { 1, 3 }, fake.PostTaskCheckIds.ToArray());
    }

    [TestMethod]
    public async Task Execute_DisabledChecks_AreSkippedByScheduler()
    {
        // Issue #105: scheduler honours StatusCheck.Enabled.
        var checks = new[]
        {
            new StatusCheck { Id = 1, Title = "alpha", Enabled = true },
            new StatusCheck { Id = 2, Title = "bravo-paused", Enabled = false },
            new StatusCheck { Id = 3, Title = "charlie", Enabled = true },
        };
        var fake = new FakeStatusCheckService(checks);

        await Build(fake).RunDueChecksAsync();

        CollectionAssert.AreEquivalent(new long[] { 1, 3 }, fake.ExecutedCheckIds.ToArray());
        CollectionAssert.AreEquivalent(new long[] { 1, 3 }, fake.PostTaskCheckIds.ToArray());
    }

    [TestMethod]
    public async Task Execute_AllChecksHealthy_RunsPostTasksForEach()
    {
        var checks = new[]
        {
            new StatusCheck { Id = 1, Title = "alpha", Enabled = true },
            new StatusCheck { Id = 2, Title = "bravo", Enabled = true },
        };
        var fake = new FakeStatusCheckService(checks);

        await Build(fake).RunDueChecksAsync();

        CollectionAssert.AreEquivalent(new long[] { 1, 2 }, fake.ExecutedCheckIds.ToArray());
        CollectionAssert.AreEquivalent(new long[] { 1, 2 }, fake.PostTaskCheckIds.ToArray());
    }

    [TestMethod]
    public async Task Execute_EmptyCheckList_DoesNotThrow()
    {
        var fake = new FakeStatusCheckService(Array.Empty<StatusCheck>());

        await Build(fake).RunDueChecksAsync();

        Assert.AreEqual(0, fake.ExecutedCheckIds.Count);
        Assert.AreEqual(0, fake.PostTaskCheckIds.Count);
    }

    [TestMethod]
    public async Task Execute_PostTasksReturnAction_PersistsActionExactlyOncePerCheck()
    {
        // Issue #75: RunPostStatusCheckTasks runs exactly once per check per
        // tick; SaveStatusCheckAction only when it returns a non-null action.
        // The exactly-once contract must hold under #78's bounded fan-out.
        var checks = new[]
        {
            new StatusCheck { Id = 1, Title = "healthy", Enabled = true },
            new StatusCheck { Id = 2, Title = "fires-webhook", Enabled = true },
            new StatusCheck { Id = 3, Title = "healthy-too", Enabled = true },
        };
        var fake = new FakeStatusCheckService(checks, returnActionForCheckId: 2);

        await Build(fake).RunDueChecksAsync();

        CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, fake.PostTaskCheckIds.ToArray(),
            "RunPostStatusCheckTasks must be called exactly once per check per tick.");
        CollectionAssert.AreEquivalent(new long[] { 2 }, fake.SavedActionCheckIds.ToArray(),
            "SaveStatusCheckAction must be called once when an action is returned.");
    }

    [TestMethod]
    public async Task Execute_PostTasksReturnNull_DoesNotPersistAction()
    {
        var checks = new[]
        {
            new StatusCheck { Id = 1, Title = "healthy-1", Enabled = true },
            new StatusCheck { Id = 2, Title = "healthy-2", Enabled = true },
        };
        var fake = new FakeStatusCheckService(checks);

        await Build(fake).RunDueChecksAsync();

        Assert.AreEqual(0, fake.SavedActionCheckIds.Count);
    }

    // ---- #78: bounded concurrency ----------------------------------------

    [TestMethod]
    public async Task Execute_BoundsInFlightChecksToConfiguredDegree()
    {
        // Each check blocks briefly so iterations overlap; the fake records the
        // peak number in flight. With degree 2 over 6 checks the peak must be
        // exactly 2 — bounded (never >2) and actually parallel (reaches 2).
        var checks = Enumerable.Range(1, 6)
            .Select(i => new StatusCheck { Id = i, Title = $"c{i}", Enabled = true })
            .ToArray();
        var fake = new FakeStatusCheckService(checks, executeDelayMs: 60);

        await Build(fake, maxConcurrency: 2).RunDueChecksAsync();

        Assert.IsTrue(fake.MaxObservedConcurrency <= 2,
            $"Fan-out must not exceed the configured degree; observed {fake.MaxObservedConcurrency}.");
        Assert.IsTrue(fake.MaxObservedConcurrency >= 2,
            $"Checks must actually run in parallel; observed {fake.MaxObservedConcurrency}.");
        CollectionAssert.AreEquivalent(
            Enumerable.Range(1, 6).Select(i => (long)i).ToArray(),
            fake.ExecutedCheckIds.ToArray());
    }

    [TestMethod]
    public async Task Execute_DegreeOfOne_RunsStrictlySerially()
    {
        var checks = Enumerable.Range(1, 4)
            .Select(i => new StatusCheck { Id = i, Title = $"c{i}", Enabled = true })
            .ToArray();
        var fake = new FakeStatusCheckService(checks, executeDelayMs: 30);

        await Build(fake, maxConcurrency: 1).RunDueChecksAsync();

        Assert.AreEqual(1, fake.MaxObservedConcurrency, "Degree 1 must serialise execution.");
    }

    [TestMethod]
    public async Task Execute_NonPositiveDegree_ClampedToOne_StillRunsAllChecks()
    {
        // A misconfigured 0/negative value must clamp to 1 (ParallelOptions
        // throws on 0), never silently stall the tick.
        var checks = new[]
        {
            new StatusCheck { Id = 1, Title = "alpha", Enabled = true },
            new StatusCheck { Id = 2, Title = "bravo", Enabled = true },
        };
        var fake = new FakeStatusCheckService(checks);

        await Build(fake, maxConcurrency: 0).RunDueChecksAsync();

        CollectionAssert.AreEquivalent(new long[] { 1, 2 }, fake.ExecutedCheckIds.ToArray());
        Assert.AreEqual(1, fake.MaxObservedConcurrency);
    }

    private sealed class NoopAutoIncidentCoordinator : IAutoIncidentCoordinator
    {
        public Task EvaluateAsync(StatusCheck check, FailType failType, bool wasDown, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<bool> ShouldDraftNowAsync(StatusCheck check, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class NoopAlertEvaluator : SuperStatus.Services.Alerts.IAlertEvaluator
    {
        public Task EvaluateAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingAlertEvaluator : SuperStatus.Services.Alerts.IAlertEvaluator
    {
        public ConcurrentQueue<long> EvaluatedCheckIds { get; } = new();

        public Task EvaluateAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default)
        {
            EvaluatedCheckIds.Enqueue(check.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStatusCheckService : IStatusCheckService
    {
        private readonly IReadOnlyList<StatusCheck> _checks;
        private readonly long? _throwOnSaveForCheckId;
        private readonly long? _returnActionForCheckId;
        private readonly int _executeDelayMs;
        private int _inFlight;
        private int _maxObserved;

        // Thread-safe: the job now fans these calls out across parallel scopes.
        public ConcurrentQueue<long> ExecutedCheckIds { get; } = new();
        public ConcurrentQueue<long> PostTaskCheckIds { get; } = new();
        public ConcurrentQueue<long> SavedActionCheckIds { get; } = new();
        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

        public FakeStatusCheckService(
            IReadOnlyList<StatusCheck> checks,
            long? throwOnSaveForCheckId = null,
            long? returnActionForCheckId = null,
            int executeDelayMs = 0)
        {
            _checks = checks;
            _throwOnSaveForCheckId = throwOnSaveForCheckId;
            _returnActionForCheckId = returnActionForCheckId;
            _executeDelayMs = executeDelayMs;
        }

        public Task<IPagedResult<StatusCheck>> GetStatusCheckSet(int page = 1, int pageSize = 0)
            => Task.FromResult<IPagedResult<StatusCheck>>(new PagedResult<StatusCheck> { Results = _checks.ToList() });

        public async Task<HistoricalStatusData> ExecuteStatusCheck(StatusCheck statusCheck)
        {
            int now = Interlocked.Increment(ref _inFlight);
            // Track the peak in-flight count (CAS so concurrent updates don't lose).
            int observed;
            while (now > (observed = Volatile.Read(ref _maxObserved)))
            {
                Interlocked.CompareExchange(ref _maxObserved, now, observed);
            }
            try
            {
                if (_executeDelayMs > 0) await Task.Delay(_executeDelayMs);
                ExecutedCheckIds.Enqueue(statusCheck.Id);
                return new HistoricalStatusData
                {
                    StatusCheckId = statusCheck.Id,
                    StatusCheck = statusCheck,
                    TimeOfCheckUTC = DateTime.UtcNow,
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<HistoricalStatusData> SaveStatusCheckResult(HistoricalStatusData statusCheckResult)
        {
            if (_throwOnSaveForCheckId == statusCheckResult.StatusCheckId)
            {
                throw new InvalidOperationException("simulated save failure");
            }
            return Task.FromResult(statusCheckResult);
        }

        public long? ThrowOnPostTaskForCheckId { get; init; }

        public Task<HistoricalStatusAction?> RunPostStatusCheckTasks(StatusCheck statusCheck, HistoricalStatusData statusCheckResult)
        {
            PostTaskCheckIds.Enqueue(statusCheck.Id);
            if (ThrowOnPostTaskForCheckId == statusCheck.Id)
            {
                throw new InvalidOperationException("simulated webhook post-task failure");
            }
            if (_returnActionForCheckId == statusCheck.Id)
            {
                return Task.FromResult<HistoricalStatusAction?>(new HistoricalStatusAction
                {
                    StatusCheckId = statusCheck.Id,
                    HistoricalStatusDataId = statusCheckResult.Id,
                    ActionType = Data.Constants.ActionType.Webhook,
                    TimeOfExecutionUTC = DateTime.UtcNow,
                });
            }
            return Task.FromResult<HistoricalStatusAction?>(null);
        }

        public Task<HistoricalStatusAction> SaveStatusCheckAction(HistoricalStatusAction action)
        {
            SavedActionCheckIds.Enqueue(action.StatusCheckId);
            return Task.FromResult(action);
        }

        // #78: the job re-queries each check by ID inside its worker scope.
        public Task<StatusCheck?> GetStatusCheck(long StatusCheckId)
            => Task.FromResult(_checks.FirstOrDefault(c => c.Id == StatusCheckId));

        // #82: empty → every check is treated as never-run (due), so these
        // fan-out tests exercise all enabled checks regardless of interval.
        // Per-check due-filtering is covered by the relational + schedule tests.
        public Task<IReadOnlyDictionary<long, DateTime>> GetLastCheckTimesAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<long, DateTime>>(new Dictionary<long, DateTime>());

        // #83: apply the same reset/increment the real service uses, in-memory.
        public Task RecordCheckOutcomeAsync(StatusCheck check, Data.Constants.FailType failType, CancellationToken cancellationToken = default)
        {
            check.ConsecutiveFailures = failType == Data.Constants.FailType.NoFail ? 0 : check.ConsecutiveFailures + 1;
            return Task.CompletedTask;
        }

        // Members not exercised by SuperStatusCheckJob — left as NotImplementedException
        // so an accidental call shows up loudly in a test.
        public Task<DayDetailViewModel?> GetDayDetailAsync(long statusCheckId, DateOnly date, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<WebhookExecutionLogViewModel>> GetRecentWebhookLogAsync(int count, bool failuresOnly, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WebhookTestFireResult> TestFireWebhookAsync(Webhook webhook, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RefreshDailyRollupsAsync(int daysBack, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IPagedResult<StatusCheckViewModel>> GetStatusCheckViewModelSet(int page = 1, int pageSize = 0) => throw new NotImplementedException();
        public Task<HistoricalStatusDataViewModel?> GetMostRecentHistoricalStatusData(long statusCheckId) => throw new NotImplementedException();
        public Task<IPagedResult<HistoricalStatusData>> GetPagedHistoricalStatusDataForStatusCheckId(long statusCheckId, int page, int pageSize = 0) => throw new NotImplementedException();
        public Task<List<HistoricalStatusData>> GetRecentTicks(long statusCheckId, int count) => throw new NotImplementedException();
        public Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataForStatusCheckIdByDays(long statusCheckId, int timeRangeInDays) => throw new NotImplementedException();
        public Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForRecentTimeRange(long statusCheckId, int timeRangeInDays) => throw new NotImplementedException();
        public Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForAllChecks(int timeRangeInDays) => throw new NotImplementedException();
        public Task<DashboardSummaryViewModel> GetDashboardSummaryAsync(int incidents30dCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StatusCheck> AddOrUpdateStatusCheck(StatusCheckViewModelBase statusCheck) => throw new NotImplementedException();
        public Task<HistoricalStatusDataViewModel> RunCheckNowAsync(long statusCheckId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StatusCheck> SetEnabledAsync(long statusCheckId, bool enabled, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> DeleteStatusCheckAsync(long statusCheckId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
