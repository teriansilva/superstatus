using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.ApiService;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Alerts;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #291 — linked webhooks + alert profiles: link-resolved dispatch for
/// both target kinds (independent per-link throttles, disabled-target skip,
/// audit FK stamping), the edit-path explicit-id link replacement, the DB FK
/// contract, the pure normalization helpers, and the Phase D edit-payload
/// rejection rules.
///
/// HISTORY (Phase D): the legacy-field translation tests (backfill dedupe /
/// naming / anchors / side-by-side resolver regression) were DELETED with the
/// code path they covered — the legacy embedded columns are gone and the
/// translation now lives in the DropLegacyEmbeddedNotificationColumns
/// migration's raw SQL (PG-only; see the migration's doc comment for the
/// coverage judgment call). Fixtures create linked entities explicitly.
/// </summary>
[TestClass]
public class LinkedTargetsPhaseATests
{
    // ---- fixtures ----------------------------------------------------------

    private static (SuperStatusDb db, SqliteConnection conn) Relational()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new SuperStatusDb(new DbContextOptionsBuilder<SuperStatusDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static StatusCheck AddCheck(SuperStatusDb db, string title, Action<StatusCheck>? configure = null)
    {
        var check = new StatusCheck
        {
            Title = title,
            StatusCheckUrl = "http://probe.test/health",
            ExpectedStatusCode = 200,
            Enabled = true,
            ServiceLogoUrl = string.Empty,
            Created = DateTime.UtcNow,
        };
        SlaTestUtil.Attach(check, slowThresholdMs: 60_000);
        configure?.Invoke(check);
        db.StatusCheckSet.Add(check);
        db.SaveChanges();
        return check;
    }

    private static StatusCheckService Service(SuperStatusDb db, Func<HttpRequestMessage, HttpResponseMessage> responder, RecordingFactory? factory = null)
        => new(new StatusCheckRepository(db),
            new HistoricalStatusDataRepository(db),
            new HistoricalStatusActionRepository(db),
            new WebhookExecutionLogRepository(db),
            new DailyStatusRollupRepository(db),
            factory ?? new RecordingFactory(responder),
            NullLogger<StatusCheckService>.Instance,
            statusCheckLinkRepository: new StatusCheckLinkRepository(db));

    private static HistoricalStatusData FailedData(StatusCheck check)
        => new(new StatusCheckResult(check, 0, 0, true), FailType.Unreachable);

    private sealed class RecordingFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public List<Uri?> Requested { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this, responder));
        private sealed class Handler(RecordingFactory owner, Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requested.Add(request.RequestUri);
                await Task.Yield();
                return f(request);
            }
        }
    }

    private sealed class RecordingEmailNotifier : IEmailNotifier
    {
        public List<string?> Overrides { get; } = [];
        public bool IsConfigured(SiteSettings settings) => true;
        public Task<EmailSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, string? recipientsOverride = null, CancellationToken cancellationToken = default)
        {
            Overrides.Add(recipientsOverride);
            return Task.FromResult(EmailSendResult.Sent(string.IsNullOrEmpty(recipientsOverride) ? "site-default" : recipientsOverride));
        }
        public Task<EmailSendResult> SendTestAsync(string? toOverride, CancellationToken cancellationToken = default)
            => Task.FromResult(EmailSendResult.Sent("test"));
    }

    private sealed class NoopWebPushNotifier : IWebPushNotifier
    {
        public bool IsConfigured(SiteSettings settings) => true;
        public Task<WebPushSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, CancellationToken cancellationToken = default)
            => Task.FromResult(WebPushSendResult.Sent("1 device(s)"));
    }

    private static AlertEvaluator Evaluator(SuperStatusDb db, RecordingEmailNotifier? email = null)
        => new(new StatusCheckLinkRepository(db),
            new AlertDeliveryLogRepository(db),
            email ?? new RecordingEmailNotifier(),
            new NoopWebPushNotifier(),
            NullLogger<AlertEvaluator>.Instance);

    // ---- Hermes PR #294 regression (reworked for explicit links) -------------

    [TestMethod]
    public async Task NoLinkHealthyTick_ClearsStaleLegacyOutageAnchor_NoRecoveryReplayAfterLink()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        // Legacy episode: outage alerted, then alerts effectively went dormant
        // (no links) before any recovery fired — the stale per-check anchor
        // must not survive a healthy tick, or a later first-time link seeds it
        // and replays the recovery for an episode that ended long ago.
        var check = AddCheck(db, "stale", c =>
        {
            c.AlertOnRecovery = true;
            c.AlertedOutageDownSinceUtc = DateTime.UtcNow.AddHours(-6);
            c.AlertLastFiredUtc = DateTime.UtcNow.AddHours(-6);
        });

        await Evaluator(db).EvaluateAsync(check, FailType.NoFail);
        Assert.IsNull(check.AlertedOutageDownSinceUtc, "healthy tick with no links ends the legacy episode");

        // Now the operator links a profile (anchors seeded from the check —
        // which the healthy tick already cleared).
        LinkedTargetTestUtil.LinkProfile(db, check, recipients: "ops@x.com", usesSiteDefaultRecipients: false);

        var email = new RecordingEmailNotifier();
        await Evaluator(db, email).EvaluateAsync(check, FailType.NoFail);

        Assert.AreEqual(0, email.Overrides.Count, "no recovery delivery for the pre-link episode");
        Assert.AreEqual(0, db.AlertDeliveryLogSet.Count(l => l.Trigger == AlertTrigger.Recovery),
            "no recovery decision row replayed from the stale anchor");
    }

    // ---- recipient normalization -------------------------------------------

    [TestMethod]
    public void NormalizeRecipients_TrimLowerSortDedupe_CommaOrSpaceSplit()
    {
        CollectionAssert.AreEqual(
            new[] { "a@x.com", "ops@x.com" },
            LinkedTargetNormalizationService.NormalizeRecipients("  Ops@X.com, a@x.com  OPS@x.com ").ToArray());
        Assert.AreEqual(0, LinkedTargetNormalizationService.NormalizeRecipients("   ").Count);
        Assert.AreEqual(0, LinkedTargetNormalizationService.NormalizeRecipients(null).Count);
    }

    [TestMethod]
    public void AutoNames_HostForWebhooks_RecipientsForProfiles_SuffixOnCollision()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "hook.example" };
        Assert.AreEqual("hook.example #2", LinkedTargetNormalizationService.AutoWebhookName("https://hook.example/fire", taken));
        Assert.AreEqual("other.example", LinkedTargetNormalizationService.AutoWebhookName("https://other.example/x", taken));

        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.AreEqual("Default recipients", LinkedTargetNormalizationService.AutoProfileName(true, new List<string>(), none));
        Assert.AreEqual("a@x.com +1", LinkedTargetNormalizationService.AutoProfileName(false, new List<string> { "a@x.com", "b@x.com" }, none));
        Assert.AreEqual("Web push", LinkedTargetNormalizationService.AutoProfileName(false, new List<string>(), none));
    }

    // ---- webhook dispatch through links ---------------------------------------

    [TestMethod]
    public async Task Webhook_NoLinks_NothingFires_NoRows()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "plain");
        var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var action = await Service(db, null!, factory).RunPostStatusCheckTasks(check, FailedData(check));

        Assert.IsNull(action);
        Assert.AreEqual(0, factory.Requested.Count, "no wire call");
        Assert.AreEqual(0, await db.WebhookExecutionLogSet.CountAsync(), "no audit rows");
    }

    [TestMethod]
    public async Task Webhook_TwoLinkedTargets_ThrottleIndependently_RowsCarryWebhookId()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "multi");
        var hot = new Webhook { Name = "hot", Url = "https://hot.example/x", IsEnabled = true, ThrottleMinutes = 0, CreatedUtc = DateTime.UtcNow };
        var throttled = new Webhook { Name = "cold", Url = "https://cold.example/x", IsEnabled = true, ThrottleMinutes = 30, CreatedUtc = DateTime.UtcNow };
        db.WebhookSet.AddRange(hot, throttled);
        await db.SaveChangesAsync();
        db.StatusCheckWebhookSet.Add(new StatusCheckWebhook { StatusCheckId = check.Id, WebhookId = hot.Id });
        db.StatusCheckWebhookSet.Add(new StatusCheckWebhook { StatusCheckId = check.Id, WebhookId = throttled.Id, LastFiredUtc = DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();
        var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var action = await Service(db, null!, factory).RunPostStatusCheckTasks(check, FailedData(check));

        Assert.IsNotNull(action, "at least one target fired");
        CollectionAssert.AreEqual(new[] { new Uri("https://hot.example/x") }, factory.Requested, "only the un-throttled target hits the wire");

        var rows = await db.WebhookExecutionLogSet.OrderBy(r => r.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count, "one audit row per linked target");
        var fired = rows.Single(r => r.Outcome == WebhookOutcome.Success);
        Assert.AreEqual(hot.Id, fired.WebhookId);
        Assert.AreEqual("https://hot.example/x", fired.TargetUrl);
        var skipped = rows.Single(r => r.Outcome == WebhookOutcome.Skipped);
        Assert.AreEqual(throttled.Id, skipped.WebhookId, "throttle skip carries the target id");
        Assert.IsNull(skipped.ErrorMessage, "a throttle skip has no reason message (unchanged #107 shape)");

        var links = await db.StatusCheckWebhookSet.OrderBy(l => l.WebhookId).ToListAsync();
        Assert.IsNotNull(links.Single(l => l.WebhookId == hot.Id).LastFiredUtc, "fired link's anchor advanced");
    }

    [TestMethod]
    public async Task Webhook_DisabledTarget_Skipped_WithReason_NoWireCall()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x");
        var webhook = new Webhook { Name = "off", Url = "https://off.example/x", IsEnabled = false, ThrottleMinutes = 0, CreatedUtc = DateTime.UtcNow };
        db.WebhookSet.Add(webhook);
        await db.SaveChangesAsync();
        db.StatusCheckWebhookSet.Add(new StatusCheckWebhook { StatusCheckId = check.Id, WebhookId = webhook.Id });
        await db.SaveChangesAsync();
        var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var action = await Service(db, null!, factory).RunPostStatusCheckTasks(check, FailedData(check));

        Assert.IsNull(action);
        Assert.AreEqual(0, factory.Requested.Count);
        var row = await db.WebhookExecutionLogSet.SingleAsync();
        Assert.AreEqual(WebhookOutcome.Skipped, row.Outcome);
        Assert.AreEqual("target disabled", row.ErrorMessage);
        Assert.AreEqual(webhook.Id, row.WebhookId);
    }

    [TestMethod]
    public async Task Webhook_LinkInsideThrottleWindow_SkipsOnNextFailure()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        // A link whose anchor sits 1 min back with a 30-min throttle (the shape
        // the drop migration's anchor carry produces for an upgrader) must skip.
        var check = AddCheck(db, "w");
        LinkedTargetTestUtil.LinkWebhook(db, check.Id, "https://hook.example/fire", throttleMinutes: 30, lastFiredUtc: DateTime.UtcNow.AddMinutes(-1));
        var factory = new RecordingFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var action = await Service(db, null!, factory).RunPostStatusCheckTasks(check, FailedData(check));

        Assert.IsNull(action);
        Assert.AreEqual(0, factory.Requested.Count, "still inside the throttle window");
        Assert.AreEqual(WebhookOutcome.Skipped, (await db.WebhookExecutionLogSet.SingleAsync()).Outcome);
    }

    // ---- alert dispatch through links ------------------------------------------

    [TestMethod]
    public async Task Alert_TwoLinkedProfiles_ThrottleIndependently_RowsCarryProfileId()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x", c => { c.AlertOnFailureThreshold = 1; c.AlertThrottleMinutes = 10; });
        var fresh = new AlertProfile { Name = "fresh", EmailEnabled = true, EmailRecipients = "a@x.com", CreatedUtc = DateTime.UtcNow };
        var hot = new AlertProfile { Name = "hot", EmailEnabled = true, EmailRecipients = "b@x.com", CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.AddRange(fresh, hot);
        await db.SaveChangesAsync();
        db.StatusCheckAlertProfileSet.Add(new StatusCheckAlertProfile { StatusCheckId = check.Id, AlertProfileId = fresh.Id });
        // 'hot' alerted recently → inside the check's 10-min throttle window.
        db.StatusCheckAlertProfileSet.Add(new StatusCheckAlertProfile { StatusCheckId = check.Id, AlertProfileId = hot.Id, AlertLastFiredUtc = DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();

        check.ConsecutiveFailures = 1;
        check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        var email = new RecordingEmailNotifier();
        await Evaluator(db, email).EvaluateAsync(check, FailType.StatusCode);

        var rows = await db.AlertDeliveryLogSet.OrderBy(r => r.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        var firedRow = rows.Single(r => r.Outcome == AlertOutcome.Fired);
        Assert.AreEqual(fresh.Id, firedRow.AlertProfileId);
        var skippedRow = rows.Single(r => r.Outcome == AlertOutcome.Skipped);
        Assert.AreEqual(hot.Id, skippedRow.AlertProfileId);
        Assert.AreEqual("throttled", skippedRow.Reason);
        CollectionAssert.AreEqual(new[] { "a@x.com" }, email.Overrides, "only the un-throttled profile delivered, with ITS recipients");
    }

    [TestMethod]
    public async Task Alert_SiteDefaultProfile_PassesEmptyOverride_ToTheNotifier()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x", c => c.AlertOnFailureThreshold = 1);
        // The explicit site-default profile (what the legacy email-on/no-
        // recipients config translated into).
        LinkedTargetTestUtil.LinkProfile(db, check, emailEnabled: true, usesSiteDefaultRecipients: true);

        check.ConsecutiveFailures = 1;
        check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        var email = new RecordingEmailNotifier();
        await Evaluator(db, email).EvaluateAsync(check, FailType.StatusCode);

        CollectionAssert.AreEqual(new string?[] { string.Empty }, email.Overrides,
            "UsesSiteDefaultRecipients → empty override = notifier falls back to the site default");
    }

    [TestMethod]
    public async Task Alert_ProfileWithNoEnabledChannel_NoDelivery_NoLogRow()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x", c => c.AlertOnFailureThreshold = 1);
        var mute = new AlertProfile { Name = "mute", EmailEnabled = false, WebPushEnabled = false, CreatedUtc = DateTime.UtcNow };
        db.AlertProfileSet.Add(mute);
        await db.SaveChangesAsync();
        db.StatusCheckAlertProfileSet.Add(new StatusCheckAlertProfile { StatusCheckId = check.Id, AlertProfileId = mute.Id });
        await db.SaveChangesAsync();

        check.ConsecutiveFailures = 1;
        check.DownSinceUtc = DateTime.UtcNow.AddSeconds(-30);
        await db.SaveChangesAsync();
        await Evaluator(db).EvaluateAsync(check, FailType.StatusCode);

        Assert.AreEqual(0, await db.AlertDeliveryLogSet.CountAsync(), "no enabled channel → no delivery + no log row");
    }

    // ---- edit-path link application ----------------------------------------------

    [TestMethod]
    public async Task Edit_ExplicitIds_ReplaceLinks_KeepingRetainedAnchors()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x");
        var a = new Webhook { Name = "a", Url = "https://a/x", CreatedUtc = DateTime.UtcNow };
        var b = new Webhook { Name = "b", Url = "https://b/x", CreatedUtc = DateTime.UtcNow };
        var c2 = new Webhook { Name = "c", Url = "https://c/x", CreatedUtc = DateTime.UtcNow };
        db.WebhookSet.AddRange(a, b, c2);
        await db.SaveChangesAsync();
        var anchor = DateTime.UtcNow.AddMinutes(-3);
        db.StatusCheckWebhookSet.Add(new StatusCheckWebhook { StatusCheckId = check.Id, WebhookId = a.Id });
        db.StatusCheckWebhookSet.Add(new StatusCheckWebhook { StatusCheckId = check.Id, WebhookId = b.Id, LastFiredUtc = anchor });
        await db.SaveChangesAsync();

        await LinkedTargetTestUtil.Normalization(db).ApplyEditLinksAsync(check, webhookIds: new[] { b.Id, c2.Id }, alertProfileIds: new long[0]);

        var links = await db.StatusCheckWebhookSet.OrderBy(l => l.WebhookId).ToListAsync();
        CollectionAssert.AreEqual(new[] { b.Id, c2.Id }, links.Select(l => l.WebhookId).ToArray());
        Assert.AreEqual(anchor, links.Single(l => l.WebhookId == b.Id).LastFiredUtc, "kept link keeps its throttle anchor");
        Assert.IsNull(links.Single(l => l.WebhookId == c2.Id).LastFiredUtc, "new link starts fresh");
    }

    [TestMethod]
    public async Task Edit_NullIds_LeaveTheFamilyUnchanged()
    {
        // Phase D: a null id list no longer means "translate the legacy fields"
        // (they're gone) — it means "don't touch this family's links".
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x");
        var webhook = LinkedTargetTestUtil.LinkWebhook(db, check.Id, "https://hook.example/fire");
        var profile = LinkedTargetTestUtil.LinkProfile(db, check, recipients: "a@x.com", usesSiteDefaultRecipients: false);

        await LinkedTargetTestUtil.Normalization(db).ApplyEditLinksAsync(check, webhookIds: null, alertProfileIds: null);

        Assert.AreEqual(webhook.Id, (await db.StatusCheckWebhookSet.SingleAsync()).WebhookId, "webhook link untouched");
        Assert.AreEqual(profile.Id, (await db.StatusCheckAlertProfileSet.SingleAsync()).AlertProfileId, "profile link untouched");

        // Empty arrays DO clear the family (the dialog's unlink-everything).
        await LinkedTargetTestUtil.Normalization(db).ApplyEditLinksAsync(check, webhookIds: new long[0], alertProfileIds: new long[0]);
        Assert.AreEqual(0, await db.StatusCheckWebhookSet.CountAsync());
        Assert.AreEqual(0, await db.StatusCheckAlertProfileSet.CountAsync());
        Assert.AreEqual(1, await db.WebhookSet.CountAsync(), "the reusable target itself stays");
    }

    // ---- DB-level FK contract -------------------------------------------------------

    [TestMethod]
    public async Task Links_CascadeOnCheckDelete_RestrictOnTargetDelete()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x");
        LinkedTargetTestUtil.LinkWebhook(db, check.Id, "https://hook.example/fire");
        LinkedTargetTestUtil.LinkProfile(db, check, recipients: "a@x.com", usesSiteDefaultRecipients: false);

        // Deleting a still-linked target is blocked at the DB (RESTRICT backstop
        // behind the API's 409). Tracker cleared so the FK fires in the DATABASE,
        // as it would for the API path (which never loads the link rows).
        db.ChangeTracker.Clear();
        var webhook = await db.WebhookSet.SingleAsync();
        db.WebhookSet.Remove(webhook);
        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Deleting the CHECK cascades its link rows; targets stay reusable.
        var tracked = await db.StatusCheckSet.SingleAsync(c => c.Id == check.Id);
        db.StatusCheckSet.Remove(tracked);
        await db.SaveChangesAsync();
        Assert.AreEqual(0, await db.StatusCheckWebhookSet.CountAsync());
        Assert.AreEqual(0, await db.StatusCheckAlertProfileSet.CountAsync());
        Assert.AreEqual(1, await db.WebhookSet.CountAsync());
        Assert.AreEqual(1, await db.AlertProfileSet.CountAsync());
    }

    // ---- payload validation (422 rules) --------------------------------------------

    [TestMethod]
    public void EditPayload_LegacyEmbeddedFields_RejectedOutright_WithReleaseNotePointer()
    {
        // #291 Phase D: the accepted-and-translated window is CLOSED — any
        // non-empty legacy field 422s, whether or not id arrays are present.
        string? webhookMsg = LinkedTargetsAdminApi.ValidateEditPayload(new StatusCheckViewModelBase
        {
            IsWebHookOnErrorEnabled = true,
            WebHookOnErrorUrl = "https://hook.example/fire",
        });
        Assert.IsNotNull(webhookMsg);
        StringAssert.Contains(webhookMsg, "webhookIds");
        StringAssert.Contains(webhookMsg, "release notes");

        StringAssert.Contains(LinkedTargetsAdminApi.ValidateEditPayload(new StatusCheckViewModelBase
        {
            WebHookOnErrorUrl = "https://hook.example/fire",   // URL alone is enough
        })!, "release notes");

        StringAssert.Contains(LinkedTargetsAdminApi.ValidateEditPayload(new StatusCheckViewModelBase
        {
            ThrottleWebHookToExecuteOnlyEveryXMinutes = 5,
        })!, "release notes");

        string? alertMsg = LinkedTargetsAdminApi.ValidateEditPayload(new StatusCheckViewModelBase
        {
            EmailAlertsEnabled = true,
            EmailRecipients = "a@x.com",
        });
        Assert.IsNotNull(alertMsg);
        StringAssert.Contains(alertMsg, "alertProfileIds");
        StringAssert.Contains(alertMsg, "release notes");

        Assert.IsNotNull(LinkedTargetsAdminApi.ValidateEditPayload(new StatusCheckViewModelBase
        {
            WebPushAlertsEnabled = true,
        }));
    }

    [TestMethod]
    public void EditPayload_IdArraysAndRoundTrippedReadOnlyIds_AreFine()
    {
        // The id-array path the rebuilt dialog posts.
        Assert.IsNull(LinkedTargetsAdminApi.ValidateEditPayload(new StatusCheckViewModelBase
        {
            WebhookIds = new List<long> { 1 },
            AlertProfileIds = new List<long> { 2 },
            SlaId = 3,
        }));

        // The READ-ONLY round-trip ids a fetched VM posts back never trip the rule.
        Assert.IsNull(LinkedTargetsAdminApi.ValidateEditPayload(new StatusCheckViewModelBase
        {
            LinkedWebhookIds = new List<long> { 1 },
            LinkedAlertProfileIds = new List<long> { 2 },
            LinkedSlaId = 3,
            EffectiveSlowThresholdMs = 1500,
        }));

        // A plain payload with no notification config at all is fine too.
        Assert.IsNull(LinkedTargetsAdminApi.ValidateEditPayload(new StatusCheckViewModelBase()));
    }

    [TestMethod]
    public void AlertProfile_EmailOnWithoutRecipientsOrDefaultFallback_Invalid()
    {
        Assert.IsNotNull(LinkedTargetsAdminApi.ValidateAlertProfile(new AlertProfileViewModel
        {
            EmailEnabled = true,
            EmailRecipients = "  ",
            UsesSiteDefaultRecipients = false,
        }), "email on + no recipients + no default fallback can never deliver → 422");

        Assert.IsNull(LinkedTargetsAdminApi.ValidateAlertProfile(new AlertProfileViewModel
        {
            EmailEnabled = true,
            UsesSiteDefaultRecipients = true,
        }));
        Assert.IsNull(LinkedTargetsAdminApi.ValidateAlertProfile(new AlertProfileViewModel
        {
            EmailEnabled = false,
            WebPushEnabled = true,
        }));
    }
}
