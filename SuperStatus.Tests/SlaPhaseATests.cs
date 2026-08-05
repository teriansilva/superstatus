using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperStatus.ApiService;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #293 — SLA entity + backfill + API + checker reads: the reduced
/// normalization service (default seeding + default assignment, idempotent,
/// dry-run preview), the exactly-one-default data-layer invariant +
/// transactional switch, the edit-path SlaId rules and the API validation.
///
/// HISTORY (Phase C / #291 Phase D): the legacy-ms translation tests
/// (find-or-create dedupe, 'Legacy N ms' naming, threshold normalization,
/// before/after slow-marking regression) were DELETED with the code path they
/// covered — the ExpectedResponseTimeInMs column is gone and the translation
/// now lives in the DropLegacyEmbeddedNotificationColumns migration's raw SQL
/// (PG-only; see the migration's doc comment for the coverage judgment call).
/// </summary>
[TestClass]
public class SlaPhaseATests
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

    private static StatusCheck AddCheck(SuperStatusDb db, string title, Sla? sla = null)
    {
        var check = new StatusCheck
        {
            Title = title,
            StatusCheckUrl = "http://probe.test/health",
            ExpectedStatusCode = 200,
            Enabled = true,
            ServiceLogoUrl = string.Empty,
            Created = DateTime.UtcNow,
            Sla = sla,
        };
        db.StatusCheckSet.Add(check);
        db.SaveChanges();
        return check;
    }

    // ---- backfill: seed + default assignment --------------------------------

    [TestMethod]
    public async Task Backfill_NoChecks_SeedsDefaultExactlyOnce()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;

        var first = await SlaTestUtil.Normalization(db).BackfillAsync(dryRun: false);
        var second = await SlaTestUtil.Normalization(db).BackfillAsync(dryRun: false);

        Assert.IsTrue(first.SeededDefault);
        Assert.AreEqual(1, first.SlasCreated);
        Assert.IsFalse(second.SeededDefault, "second run finds the default and does nothing");
        Assert.AreEqual(0, second.SlasCreated);
        var sla = await db.SlaSet.SingleAsync();
        Assert.AreEqual("Default", sla.Name);
        Assert.IsTrue(sla.IsDefault);
        Assert.AreEqual(1000, sla.SlowThresholdMs);
        Assert.AreEqual(100, sla.TargetUptimePercent);
        Assert.AreEqual(100, sla.CriticalUptimePercent);
    }

    [TestMethod]
    public async Task Backfill_AssignsTheDefault_ToSlaLessChecks_Idempotent_OneReportRow()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        // Phase C semantics: SLA-less checks (rows created outside the API —
        // the legacy-ms translation happened in the drop migration's SQL)
        // link to the DEFAULT; existing links are never touched.
        var custom = new Sla { Name = "Gold", TargetUptimePercent = 99.9, CriticalUptimePercent = 99, SlowThresholdMs = 250, CreatedUtc = DateTime.UtcNow };
        db.SlaSet.Add(custom);
        db.SaveChanges();
        var linked = AddCheck(db, "linked", custom);
        var orphanA = AddCheck(db, "orphan-a");
        var orphanB = AddCheck(db, "orphan-b");

        var first = await SlaTestUtil.Normalization(db).BackfillAsync(dryRun: false);
        var second = await SlaTestUtil.Normalization(db).BackfillAsync(dryRun: false);

        Assert.IsTrue(first.SeededDefault, "no default existed → seeded");
        Assert.AreEqual(2, first.AssignmentsMade);
        Assert.AreEqual(0, second.AssignmentsMade, "second run finds nothing to do");

        var def = await db.SlaSet.SingleAsync(s => s.IsDefault);
        Assert.AreEqual("Default", def.Name);
        Assert.AreEqual(def.Id, (await db.StatusCheckSet.FindAsync(orphanA.Id))!.SlaId);
        Assert.AreEqual(def.Id, (await db.StatusCheckSet.FindAsync(orphanB.Id))!.SlaId);
        Assert.AreEqual(custom.Id, (await db.StatusCheckSet.FindAsync(linked.Id))!.SlaId, "existing link untouched");
        Assert.AreEqual(1, await db.BackfillReportSet.CountAsync(), "only the run that changed something writes a report");
    }

    [TestMethod]
    public async Task Backfill_PersistsReportRow_WithDistinctKind_AndPerCheckJson()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        AddCheck(db, "edge-api");

        await SlaTestUtil.Normalization(db).BackfillAsync(dryRun: false);

        var report = await db.BackfillReportSet.SingleAsync();
        Assert.AreEqual(SlaNormalizationService.BackfillKind, report.Kind);
        StringAssert.Contains(report.SummaryJson, "edge-api");
        StringAssert.Contains(report.SummaryJson, "Default");
    }

    [TestMethod]
    public async Task Backfill_DryRunPreview_ReportsSummary_WritesNothing()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "a");

        var preview = await SlaTestUtil.Normalization(db).BackfillAsync(dryRun: true);

        Assert.IsTrue(preview.DryRun);
        Assert.IsTrue(preview.SeededDefault);
        Assert.AreEqual(1, preview.SlasCreated);
        Assert.AreEqual(1, preview.AssignmentsMade);
        Assert.AreEqual(0, await db.SlaSet.CountAsync(), "preview writes nothing");
        Assert.AreEqual(0, await db.BackfillReportSet.CountAsync());
        Assert.IsNull((await db.StatusCheckSet.AsNoTracking().SingleAsync(c => c.Id == check.Id)).SlaId);
    }

    // ---- the threshold read is link-only ---------------------------------------

    [TestMethod]
    public void GetSlowThresholdMs_MissingSla_ThrowsInvariantViolation()
    {
        // Unreachable after the startup backfill — but a check that slipped
        // through must fail loudly, not silently classify with nothing.
        var check = new StatusCheck { Id = 7, Title = "orphan" };
        Assert.ThrowsException<InvalidOperationException>(() => StatusCheckService.GetSlowThresholdMs(check));
    }

    [TestMethod]
    public async Task GetSlowThresholdMs_ReadsTheLinkedSla()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x", SlaTestUtil.Mirror(500));

        var reloaded = await new StatusCheckRepository(db).GetStatusCheckById(check.Id);
        Assert.AreEqual(500, StatusCheckService.GetSlowThresholdMs(reloaded!));
    }

    // ---- exactly-one-default at the DATA layer -----------------------------------

    [TestMethod]
    public async Task DbIndex_SecondDefaultInsert_Rejected()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        db.SlaSet.Add(SlaTestUtil.Mirror(100));
        var firstDefault = SlaTestUtil.Mirror(200); firstDefault.IsDefault = true;
        db.SlaSet.Add(firstDefault);
        await db.SaveChangesAsync();

        var secondDefault = SlaTestUtil.Mirror(300); secondDefault.IsDefault = true;
        db.SlaSet.Add(secondDefault);
        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => db.SaveChangesAsync(),
            "the partial unique index rejects a second IsDefault row even on a raw insert");
    }

    [TestMethod]
    public async Task SetDefault_TransactionalSwitch_LeavesExactlyOne_AcrossSequentialSwitches()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var a = SlaTestUtil.Mirror(100); a.IsDefault = true;
        var b = SlaTestUtil.Mirror(200);
        var c = SlaTestUtil.Mirror(300);
        db.SlaSet.AddRange(a, b, c);
        await db.SaveChangesAsync();
        var svc = SlaTestUtil.Normalization(db);

        Assert.IsTrue(await svc.SetDefaultAsync(b.Id));
        Assert.AreEqual(1, await db.SlaSet.CountAsync(s => s.IsDefault));
        Assert.IsTrue((await db.SlaSet.FindAsync(b.Id))!.IsDefault);

        // Concurrent-ish back-to-back switches: each leaves exactly one.
        Assert.IsTrue(await svc.SetDefaultAsync(c.Id));
        Assert.IsTrue(await svc.SetDefaultAsync(b.Id));
        Assert.IsTrue(await svc.SetDefaultAsync(c.Id));
        Assert.AreEqual(1, await db.SlaSet.CountAsync(s => s.IsDefault));
        Assert.IsTrue((await db.SlaSet.FindAsync(c.Id))!.IsDefault);

        Assert.IsFalse(await svc.SetDefaultAsync(999_999), "unknown id → false → 404 at the API");
        Assert.IsTrue(await svc.SetDefaultAsync(c.Id), "re-defaulting the default is a no-op success");
    }

    // ---- delete guards --------------------------------------------------------------

    [TestMethod]
    public async Task Delete_LinkedSla_BlockedByDbRestrict_AndTitlesFeedThe409()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        AddCheck(db, "prod-api");
        SlaTestUtil.RunBackfill(db);

        // The titles the endpoint embeds in its 409 LinkedEntitySummary.
        var titles = await new StatusCheckRepository(db).GetSlaLinkedCheckTitlesAsync();
        var sla = await db.SlaSet.SingleAsync();
        CollectionAssert.AreEqual(new[] { "prod-api" }, titles[sla.Id]);
        Assert.AreEqual(1, LinkedEntitySummary.From(titles[sla.Id]).UsedByCount);

        // DB RESTRICT backstop: a raw relational delete of a referenced SLA fails.
        db.ChangeTracker.Clear();
        var tracked = await db.SlaSet.SingleAsync();
        db.SlaSet.Remove(tracked);
        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Unlinked + non-default deletes cleanly.
        var unlinked = SlaTestUtil.Mirror(9_999);
        db.SlaSet.Add(unlinked);
        await db.SaveChangesAsync();
        db.SlaSet.Remove(unlinked);
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await db.SlaSet.CountAsync());
    }

    [TestMethod]
    public void DeleteVerdict_DefaultIs422_LinkedIs409_DefaultWins()
    {
        var def = SlaTestUtil.Mirror(1000); def.IsDefault = true;
        var plain = SlaTestUtil.Mirror(500);

        Assert.AreEqual(SlaAdminApi.SlaDeleteVerdict.IsDefault, SlaAdminApi.ValidateDelete(def, null),
            "deleting the default → 422");
        Assert.AreEqual(SlaAdminApi.SlaDeleteVerdict.IsDefault, SlaAdminApi.ValidateDelete(def, new List<string> { "api" }),
            "a linked default still reports IsDefault — that's the real blocker");
        Assert.AreEqual(SlaAdminApi.SlaDeleteVerdict.Linked, SlaAdminApi.ValidateDelete(plain, new List<string> { "api", "web" }),
            "deleting a referenced SLA → 409 with the linked names");
        Assert.AreEqual(SlaAdminApi.SlaDeleteVerdict.Ok, SlaAdminApi.ValidateDelete(plain, new List<string>()));
        Assert.AreEqual(SlaAdminApi.SlaDeleteVerdict.Ok, SlaAdminApi.ValidateDelete(plain, null));
    }

    // ---- edit path: the SlaId rules ----------------------------------------------------

    [TestMethod]
    public async Task Edit_ExplicitSlaId_RelinksTheCheck()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x");
        SlaTestUtil.RunBackfill(db);
        var custom = new Sla { Name = "Gold", TargetUptimePercent = 99.9, CriticalUptimePercent = 99, SlowThresholdMs = 250, CreatedUtc = DateTime.UtcNow };
        db.SlaSet.Add(custom);
        await db.SaveChangesAsync();

        var tracked = await new StatusCheckRepository(db).GetStatusCheckById(check.Id);
        await SlaTestUtil.Normalization(db).ApplyEditSlaAsync(tracked!, requestedSlaId: custom.Id, isNewCheck: false);

        Assert.AreEqual(custom.Id, (await db.StatusCheckSet.AsNoTracking().SingleAsync(c => c.Id == check.Id)).SlaId);
        Assert.AreEqual(2, await db.SlaSet.CountAsync(), "no extra entity minted");
    }

    [TestMethod]
    public async Task Edit_NullSlaId_ExistingCheck_KeepsCurrentLink()
    {
        // Phase C: SlaId omitted on an existing check = "don't touch the link"
        // (there is no legacy-ms fallback to translate anymore).
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var custom = new Sla { Name = "Gold", TargetUptimePercent = 99.9, CriticalUptimePercent = 99, SlowThresholdMs = 250, IsDefault = true, CreatedUtc = DateTime.UtcNow };
        db.SlaSet.Add(custom);
        db.SaveChanges();
        var check = AddCheck(db, "x", custom);

        var tracked = await new StatusCheckRepository(db).GetStatusCheckById(check.Id);
        await SlaTestUtil.Normalization(db).ApplyEditSlaAsync(tracked!, requestedSlaId: null, isNewCheck: false);

        Assert.AreEqual(custom.Id, (await db.StatusCheckSet.AsNoTracking().SingleAsync(c => c.Id == check.Id)).SlaId,
            "an edit without slaId never re-links");
        Assert.AreEqual(1, await db.SlaSet.CountAsync(), "and never mints an entity");
    }

    [TestMethod]
    public async Task Edit_NewCheck_NullSlaId_LinksToTheDefaultSla()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        SlaTestUtil.RunBackfill(db);   // seeds the Default
        var def = await db.SlaSet.SingleAsync(s => s.IsDefault);

        var fresh = AddCheck(db, "fresh");
        await SlaTestUtil.Normalization(db).ApplyEditSlaAsync(fresh, requestedSlaId: null, isNewCheck: true);

        Assert.AreEqual(def.Id, (await db.StatusCheckSet.AsNoTracking().SingleAsync(c => c.Id == fresh.Id)).SlaId);
        Assert.AreEqual(1, await db.SlaSet.CountAsync(), "no extra entity");
    }

    [TestMethod]
    public async Task Edit_UnknownSlaId_ThrowsBehindTheApis422()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x");
        // The endpoint answers 422 before calling the service; the service
        // throws as the backstop so a bypass can't link to nothing.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => SlaTestUtil.Normalization(db).ApplyEditSlaAsync(check, requestedSlaId: 12345, isNewCheck: false));
    }

    // ---- API validation (422 rules) ------------------------------------------------

    [TestMethod]
    public void ValidateSla_RejectsInvalidShapes_AcceptsValid()
    {
        Assert.IsNotNull(SlaAdminApi.ValidateSla(new SlaViewModel { Name = "  ", SlowThresholdMs = 100 }),
            "empty name → 422");
        StringAssert.Contains(SlaAdminApi.ValidateSla(new SlaViewModel
        {
            Name = "x",
            TargetUptimePercent = 99,
            CriticalUptimePercent = 99.5,
            SlowThresholdMs = 100,
        })!, "criticalUptimePercent", "Critical > Target → 422");
        StringAssert.Contains(SlaAdminApi.ValidateSla(new SlaViewModel
        {
            Name = "x",
            TargetUptimePercent = 100,
            CriticalUptimePercent = 100,
            SlowThresholdMs = 0,
        })!, "slowThresholdMs", "threshold < 1 → 422");
        Assert.IsNotNull(SlaAdminApi.ValidateSla(new SlaViewModel { Name = "x", TargetUptimePercent = 101, CriticalUptimePercent = 100, SlowThresholdMs = 1 }));
        Assert.IsNotNull(SlaAdminApi.ValidateSla(new SlaViewModel { Name = "x", TargetUptimePercent = 100, CriticalUptimePercent = -1, SlowThresholdMs = 1 }));

        Assert.IsNull(SlaAdminApi.ValidateSla(new SlaViewModel
        {
            Name = "Gold",
            TargetUptimePercent = 99.9,
            CriticalUptimePercent = 99,
            SlowThresholdMs = 1,
        }));
        Assert.IsNull(SlaAdminApi.ValidateSla(new SlaViewModel
        {
            Name = "Equal targets are fine",
            TargetUptimePercent = 100,
            CriticalUptimePercent = 100,
            SlowThresholdMs = 1000,
        }));
    }

    // ---- VM carries the effective threshold ----------------------------------------

    [TestMethod]
    public async Task ViewModel_CarriesEffectiveThresholdAndSlaIdentity_FromTheLink()
    {
        var (db, conn) = Relational(); using var _ = db; using var __ = conn;
        var check = AddCheck(db, "x");
        SlaTestUtil.RunBackfill(db);
        // Operator later tightens the SLA — the VM (and so the detail page's
        // ComputeFailType + the public API) must follow the SLA.
        var sla = await db.SlaSet.SingleAsync();
        sla.SlowThresholdMs = 350;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await new StatusCheckRepository(db).GetStatusCheckById(check.Id);
        var vm = new StatusCheckViewModel(loaded!, null);

        Assert.AreEqual(350, vm.EffectiveSlowThresholdMs, "the read-only effective threshold follows the SLA");
        Assert.AreEqual(sla.Id, vm.LinkedSlaId);
        Assert.AreEqual(sla.Name, vm.LinkedSlaName);
        Assert.IsNull(vm.SlaId, "the write-side property is never populated on reads");
    }
}
