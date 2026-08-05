using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SuperStatus.Data.Migrations.SuperStatusDbMigration;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #291 Phase D / #293 Phase C — the DropLegacyEmbeddedNotificationColumns
/// migration. JUDGMENT CALL (documented in the migration too): the intended
/// "seed a legacy check on Sqlite, run Database.Migrate(), assert translation"
/// test is NOT feasible — the historic migration chain contains PostgreSQL-only
/// SQL (NOW() defaults in AddStatusCheckGridFields/AddOnboarded, ::int casts)
/// that Sqlite cannot execute, so the chain has never been Sqlite-runnable.
/// Instead this verifies the migration's STRUCTURE on both providers:
///   - on PostgreSQL the raw translation SQL runs BEFORE any column drop and
///     mirrors the deleted C# normalization semantics (dedupe identities,
///     skip-already-linked guards, anchor carries, default-SLA rule);
///   - on Sqlite (the test provider) the SQL is skipped entirely, so the
///     migration degrades to plain column drops;
///   - exactly the seven legacy columns drop, and Down() restores them
///     (schema-only — the data loss is the point).
/// The translation semantics themselves were covered by the Phase A test
/// suites while the C# path existed; the SQL mirrors them statement-for-statement.
/// </summary>
[TestClass]
public class DropLegacyColumnsMigrationTests
{
    private static readonly string[] LegacyColumns =
    {
        "EmailAlertsEnabled",
        "EmailRecipients",
        "ExpectedResponseTimeInMs",
        "IsWebHookOnErrorEnabled",
        "ThrottleWebHookToExecuteOnlyEveryXMinutes",
        "WebHookOnErrorUrl",
        "WebPushAlertsEnabled",
    };

    private static IReadOnlyList<MigrationOperation> Ops(string provider)
        => new DropLegacyEmbeddedNotificationColumns { ActiveProvider = provider }.UpOperations;

    [TestMethod]
    public void OnPostgres_TranslationSqlRunsBeforeTheDrops()
    {
        var ops = Ops("Npgsql.EntityFrameworkCore.PostgreSQL");

        int lastSql = ops.Select((op, i) => (op, i)).Where(x => x.op is SqlOperation).Max(x => x.i);
        int firstDrop = ops.Select((op, i) => (op, i)).Where(x => x.op is DropColumnOperation).Min(x => x.i);
        Assert.IsTrue(lastSql < firstDrop, "every raw-SQL statement must run while the legacy columns still exist");

        string sql = string.Join("\n", ops.OfType<SqlOperation>().Select(o => o.Sql));

        // All three target families are written.
        StringAssert.Contains(sql, "INSERT INTO \"WebhookSet\"");
        StringAssert.Contains(sql, "INSERT INTO \"StatusCheckWebhookSet\"");
        StringAssert.Contains(sql, "INSERT INTO \"AlertProfileSet\"");
        StringAssert.Contains(sql, "INSERT INTO \"StatusCheckAlertProfileSet\"");
        StringAssert.Contains(sql, "INSERT INTO \"SlaSet\"");
        StringAssert.Contains(sql, "SET \"SlaId\"");

        // Idempotency: already-linked checks are skipped per family, so an
        // instance that ran the C# backfills finds nothing to do.
        StringAssert.Contains(sql, "NOT EXISTS (SELECT 1 FROM \"StatusCheckWebhookSet\"");
        StringAssert.Contains(sql, "NOT EXISTS (SELECT 1 FROM \"StatusCheckAlertProfileSet\"");
        StringAssert.Contains(sql, "WHERE sc.\"SlaId\" IS NULL");

        // Dedupe identities mirror the deleted C# normalization.
        StringAssert.Contains(sql, "w.\"Url\" = rec.url AND w.\"ThrottleMinutes\" = rec.throttle");
        StringAssert.Contains(sql, "p.\"EmailEnabled\" = rec.email_on");
        StringAssert.Contains(sql, "p.\"UsesSiteDefaultRecipients\" = uses_default");
        StringAssert.Contains(sql, "s.\"TargetUptimePercent\" = 100");

        // Anchor carries + the legacy ms floor + the first-default rule.
        StringAssert.Contains(sql, "max(a.\"TimeOfExecutionUTC\")");
        StringAssert.Contains(sql, "rec.episode_anchor, rec.fired_anchor");
        StringAssert.Contains(sql, "GREATEST(sc.\"ExpectedResponseTimeInMs\", 1)");
        StringAssert.Contains(sql, "NOT has_default");
    }

    [TestMethod]
    public void OnSqlite_SqlIsSkipped_OnlyColumnDropsRemain()
    {
        var ops = Ops("Microsoft.EntityFrameworkCore.Sqlite");
        Assert.AreEqual(0, ops.OfType<SqlOperation>().Count(), "plpgsql cannot run on Sqlite — provider-guarded");
        Assert.AreEqual(LegacyColumns.Length, ops.Count, "nothing but the drops");
    }

    [TestMethod]
    public void DropsExactlyTheSevenLegacyColumns_AndDownRestoresThem()
    {
        var ops = Ops("Npgsql.EntityFrameworkCore.PostgreSQL");
        var dropped = ops.OfType<DropColumnOperation>().Select(o => o.Name).OrderBy(n => n).ToArray();
        CollectionAssert.AreEqual(LegacyColumns, dropped);
        Assert.IsTrue(ops.OfType<DropColumnOperation>().All(o => o.Table == "StatusCheckSet"));

        var down = new DropLegacyEmbeddedNotificationColumns { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" }.DownOperations;
        var restored = down.OfType<AddColumnOperation>().Select(o => o.Name).OrderBy(n => n).ToArray();
        CollectionAssert.AreEqual(LegacyColumns, restored, "Down() restores the schema (values are gone — that's the point)");
    }
}
