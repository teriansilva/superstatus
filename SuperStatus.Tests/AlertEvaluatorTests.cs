using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Alerts;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #241/#253: the alert evaluator's persistence — it writes one
/// AlertDeliveryLog per channel per fired episode, dedups within an outage, and
/// records recovery, against SQLite. Phase A is delivery-free (rows are "Fired").
/// </summary>
[TestClass]
public class AlertEvaluatorTests
{
    private static (AlertEvaluator eval, SuperStatusDb db, SqliteConnection conn) Build(
        IEmailNotifier? notifier = null, IWebPushNotifier? webPush = null)
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var eval = new AlertEvaluator(
            new StatusCheckLinkRepository(db),
            new AlertDeliveryLogRepository(db),
            notifier ?? new StubEmailNotifier(),
            webPush ?? new StubWebPushNotifier(),
            NullLogger<AlertEvaluator>.Instance);
        return (eval, db, conn);
    }

    private sealed class StubEmailNotifier : IEmailNotifier
    {
        public EmailSendResult Result { get; set; } = EmailSendResult.Sent("ops@example.com");
        public int Calls { get; private set; }
        public bool IsConfigured(SiteSettings settings) => true;
        public Task<EmailSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, string? recipientsOverride = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
        public Task<EmailSendResult> SendTestAsync(string? toOverride, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private sealed class StubWebPushNotifier : IWebPushNotifier
    {
        public WebPushSendResult Result { get; set; } = WebPushSendResult.Sent("1 device(s)");
        public int Calls { get; private set; }
        public bool IsConfigured(SiteSettings settings) => true;
        public Task<WebPushSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    [TestMethod]
    public async Task EmailFire_sendsEmail_recordsFiredWithTarget()
    {
        var stub = new StubEmailNotifier { Result = EmailSendResult.Sent("ops@example.com") };
        var (eval, db, conn) = Build(stub); using var _ = db; using var __ = conn;
        var check = AddCheck(db);

        check.ConsecutiveFailures = 2; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        Assert.AreEqual(1, stub.Calls, "email channel actually attempts delivery");
        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(AlertOutcome.Fired, row.Outcome);
        Assert.AreEqual("ops@example.com", row.Target);
    }

    [TestMethod]
    public async Task EmailFire_sendFails_recordsFailedWithError()
    {
        var stub = new StubEmailNotifier { Result = EmailSendResult.Failed("ops@example.com", "smtp refused") };
        var (eval, db, conn) = Build(stub); using var _ = db; using var __ = conn;
        var check = AddCheck(db);

        check.ConsecutiveFailures = 2; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(AlertOutcome.Failed, row.Outcome, "an attempted-but-errored send is Failed");
        Assert.AreEqual("smtp refused", row.ErrorMessage);
    }

    [TestMethod]
    public async Task EmailFire_skippedGuard_recordsSkipped_notFailed()
    {
        // Regression (#256): an intentional guard skip (SMTP not configured / no
        // recipients) must log Skipped — not show as a delivery outage.
        var stub = new StubEmailNotifier { Result = EmailSendResult.Skipped("SMTP not configured") };
        var (eval, db, conn) = Build(stub); using var _ = db; using var __ = conn;
        var check = AddCheck(db);

        check.ConsecutiveFailures = 2; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(AlertOutcome.Skipped, row.Outcome, "a guard skip is not a failure");
        Assert.AreEqual("SMTP not configured", row.Reason);
        Assert.IsNull(row.ErrorMessage);
    }

    [TestMethod]
    public async Task WebPushFire_sends_recordsFiredWithDeviceTarget()
    {
        var stub = new StubWebPushNotifier { Result = WebPushSendResult.Sent("2 device(s)") };
        var (eval, db, conn) = Build(webPush: stub); using var _ = db; using var __ = conn;
        var check = AddCheck(db, email: false, push: true);

        check.ConsecutiveFailures = 2; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        Assert.AreEqual(1, stub.Calls, "web-push channel actually attempts delivery");
        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(AlertChannel.WebPush, row.Channel);
        Assert.AreEqual(AlertOutcome.Fired, row.Outcome);
        Assert.AreEqual("2 device(s)", row.Target);
    }

    [TestMethod]
    public async Task WebPushFire_sendFails_recordsFailedWithError()
    {
        var stub = new StubWebPushNotifier { Result = WebPushSendResult.Failed("0 device(s)", "push service 500") };
        var (eval, db, conn) = Build(webPush: stub); using var _ = db; using var __ = conn;
        var check = AddCheck(db, email: false, push: true);

        check.ConsecutiveFailures = 2; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(AlertOutcome.Failed, row.Outcome, "an attempted-but-errored fan-out is Failed");
        Assert.AreEqual("push service 500", row.ErrorMessage);
    }

    [TestMethod]
    public async Task WebPushFire_skippedGuard_recordsSkipped_notFailed()
    {
        // No VAPID keys / no subscribed devices is a guard skip — not a delivery outage.
        var stub = new StubWebPushNotifier { Result = WebPushSendResult.Skipped("no subscribed devices") };
        var (eval, db, conn) = Build(webPush: stub); using var _ = db; using var __ = conn;
        var check = AddCheck(db, email: false, push: true);

        check.ConsecutiveFailures = 2; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(AlertOutcome.Skipped, row.Outcome, "a guard skip is not a failure");
        Assert.AreEqual("no subscribed devices", row.Reason);
        Assert.IsNull(row.ErrorMessage);
    }

    private static StatusCheck AddCheck(SuperStatusDb db, bool email = true, bool push = false, Action<StatusCheck>? configure = null)
    {
        var check = new StatusCheck
        {
            Title = "svc",
            StatusCheckUrl = "https://svc.test",
            ServiceLogoUrl = string.Empty,
            AlertOnFailureThreshold = 2,
            AlertOnRecovery = true,
        };
        configure?.Invoke(check);
        db.StatusCheckSet.Add(check);
        db.SaveChanges();
        // #291 Phase D: dispatch resolves through links only — fixtures link
        // an explicit profile (the site-default-email shape mirrors what the
        // legacy translation produced) and seed the link anchors from the
        // check's columns, like the drop migration's raw SQL does.
        if (email || push) LinkedTargetTestUtil.LinkProfile(db, check, emailEnabled: email, usesSiteDefaultRecipients: email, webPushEnabled: push);
        return check;
    }

    /// <summary>#291: the per-link anchors that replaced the per-check ones.</summary>
    private static StatusCheckAlertProfile LinkOf(SuperStatusDb db, StatusCheck check)
        => db.StatusCheckAlertProfileSet.Single(l => l.StatusCheckId == check.Id);

    private static async Task<int> RowCount(SuperStatusDb db) => await db.AlertDeliveryLogSet.CountAsync();

    [TestMethod]
    public async Task Fires_once_perOutageEpisode_thenRecovers()
    {
        var (eval, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = AddCheck(db);

        // Tick 1 — failing, threshold reached (sim of RecordCheckOutcomeAsync state).
        check.ConsecutiveFailures = 2;
        check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);
        Assert.AreEqual(1, await RowCount(db), "one fired row");
        var row = await db.AlertDeliveryLogSet.SingleAsync();
        Assert.AreEqual(AlertChannel.Email, row.Channel);
        Assert.AreEqual(AlertTrigger.Failure, row.Trigger);
        Assert.AreEqual(AlertOutcome.Fired, row.Outcome);
        Assert.IsNotNull(LinkOf(db, check).AlertedOutageDownSinceUtc);

        // Tick 2 — still failing, same episode → no new alert (dedup).
        check.ConsecutiveFailures = 3;
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);
        Assert.AreEqual(1, await RowCount(db), "deduped within the outage episode");

        // Recovery — healthy result clears the failure state (sim), recovery fires once.
        check.ConsecutiveFailures = 0;
        check.DownSinceUtc = null;
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.NoFail);
        Assert.AreEqual(2, await RowCount(db));
        var recovery = await db.AlertDeliveryLogSet.OrderByDescending(x => x.Id).FirstAsync();
        Assert.AreEqual(AlertTrigger.Recovery, recovery.Trigger);
        Assert.IsNull(LinkOf(db, check).AlertedOutageDownSinceUtc, "episode cleared");
    }

    [TestMethod]
    public async Task ThrottledOutage_logsOneSkipped_neverRepeatsOrFiresLater()
    {
        // Regression (#254): a throttled outage must consume the episode — one
        // Skipped row, no repeat every tick, and no delayed Fired once the window
        // expires.
        var (eval, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, configure: c =>
        {
            c.AlertOnFailureThreshold = 1;
            c.AlertThrottleMinutes = 10;
            c.AlertLastFiredUtc = DateTime.UtcNow.AddMinutes(-1); // recent prior alert → throttled
        });

        // Down, threshold met, but within the throttle window → one Skipped row.
        check.ConsecutiveFailures = 1;
        check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);
        Assert.AreEqual(1, await RowCount(db));
        Assert.AreEqual(AlertOutcome.Skipped, (await db.AlertDeliveryLogSet.SingleAsync()).Outcome);
        Assert.IsNotNull(LinkOf(db, check).AlertedOutageDownSinceUtc, "episode consumed");

        // Next tick, same episode → deduped, no new row.
        check.ConsecutiveFailures = 2;
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);
        Assert.AreEqual(1, await RowCount(db), "does not re-log a Skipped row every tick");

        // The throttle window passes, but it's still the SAME episode → no late Fired.
        LinkOf(db, check).AlertLastFiredUtc = DateTime.UtcNow.AddMinutes(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);
        Assert.AreEqual(1, await RowCount(db));
        Assert.IsFalse(await db.AlertDeliveryLogSet.AnyAsync(x => x.Outcome == AlertOutcome.Fired),
            "a suppressed episode never fires late");
    }

    [TestMethod]
    public async Task SuppressedThenHealthy_clearsMarker_withoutRecoveryLog()
    {
        var (eval, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, configure: c =>
        {
            c.AlertOnFailureThreshold = 1;
            c.AlertThrottleMinutes = 10;
            c.AlertLastFiredUtc = DateTime.UtcNow.AddMinutes(-1); // throttled
        });

        // Throttled outage → one Skipped, episode consumed.
        check.ConsecutiveFailures = 1; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);
        Assert.IsNotNull(LinkOf(db, check).AlertedOutageDownSinceUtc);

        // Recovers → the suppressed (never-alerted) episode emits NO recovery row,
        // but the marker is cleared so it can't replay later.
        check.ConsecutiveFailures = 0; check.DownSinceUtc = null;
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.NoFail);
        Assert.IsNull(LinkOf(db, check).AlertedOutageDownSinceUtc, "stale marker cleared on recovery");
        Assert.IsFalse(await db.AlertDeliveryLogSet.AnyAsync(x => x.Trigger == AlertTrigger.Recovery),
            "no recovery announced for an outage that only got throttled");
    }

    [TestMethod]
    public async Task ChannelsDisabledThenHealthy_clearsStaleMarker()
    {
        var (eval, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, configure: c => c.AlertOnFailureThreshold = 1);

        // Alert fires while email is enabled → marker set.
        check.ConsecutiveFailures = 1; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.Unreachable);
        Assert.IsNotNull(LinkOf(db, check).AlertedOutageDownSinceUtc);

        // Channel disabled (on the linked profile since #291), then the check
        // recovers — the stale marker must still be cleared so re-enabling a
        // channel later can't replay an old recovery.
        db.AlertProfileSet.Single().EmailEnabled = false;
        check.ConsecutiveFailures = 0; check.DownSinceUtc = null;
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.NoFail);
        Assert.IsNull(LinkOf(db, check).AlertedOutageDownSinceUtc, "stale marker cleared even with channels off");
    }

    [TestMethod]
    public async Task NoChannelEnabled_writesNothing_evenWhenFailing()
    {
        var (eval, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, email: false);

        check.ConsecutiveFailures = 9;
        check.DownSinceUtc = DateTime.UtcNow.AddMinutes(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        Assert.AreEqual(0, await RowCount(db), "no channel → inert engine, no behavior change");
    }

    [TestMethod]
    public async Task BothChannels_fireTogether_oneRowEach()
    {
        var (eval, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, push: true);

        check.ConsecutiveFailures = 2;
        check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.StatusCode);

        Assert.AreEqual(2, await RowCount(db));
        var channels = await db.AlertDeliveryLogSet.Select(x => x.Channel).OrderBy(x => x).ToListAsync();
        CollectionAssert.AreEqual(new[] { AlertChannel.Email, AlertChannel.WebPush }, channels);
    }

    [TestMethod]
    public async Task Flapping_downUpDown_producesTwoOutages_andOneRecoveryEach()
    {
        var (eval, db, conn) = Build(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, configure: c => c.AlertOnFailureThreshold = 1);

        // Down #1
        check.ConsecutiveFailures = 1; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-40);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.Unreachable);

        // Up #1 (recovery)
        check.ConsecutiveFailures = 0; check.DownSinceUtc = null;
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.NoFail);

        // Down #2 — a NEW episode (different DownSinceUtc) → fires again.
        check.ConsecutiveFailures = 1; check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-5);
        await db.SaveChangesAsync();
        await eval.EvaluateAsync(check, FailType.Unreachable);

        var triggers = await db.AlertDeliveryLogSet.OrderBy(x => x.Id).Select(x => x.Trigger).ToListAsync();
        CollectionAssert.AreEqual(
            new[] { AlertTrigger.Failure, AlertTrigger.Recovery, AlertTrigger.Failure },
            triggers,
            "one alert + one recovery per episode, even while flapping");
    }
}
