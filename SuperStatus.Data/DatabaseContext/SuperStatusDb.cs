using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.DatabaseContext
{
    public class SuperStatusDb(DbContextOptions<SuperStatusDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {

            // Issue #80: switch from ClientCascade to Cascade so PostgreSQL
            // handles HistoricalStatusAction removal via a native
            // ON DELETE CASCADE FK instead of EF's change tracker loading
            // each related row to delete it. Required for the cleanup
            // job's switch to ExecuteDeleteAsync (which bypasses the
            // change tracker entirely).
            modelBuilder.Entity<HistoricalStatusData>()
             .HasOne(x => x.HistoricalStatusAction)
             .WithOne(y => y.HistoricalStatusData)
             .HasForeignKey<HistoricalStatusAction>(y => y.HistoricalStatusDataId)
             .OnDelete(DeleteBehavior.Cascade);

            // Issue #79. Two hot query shapes hit HistoricalStatusDataSet on
            // every poll:
            //   - GetMostRecentHistoricalStatusData(checkId)
            //         WHERE StatusCheckId = … ORDER BY TimeOfCheckUTC DESC
            //         LIMIT 1
            //   - cleanup job
            //         WHERE TimeOfCheckUTC < cutoff
            // Without indexes both are full table scans on a table that grows
            // ~259k rows/day at 30 checks × 10 s cadence. The composite index
            // covers GetMostRecent + the day-grouped history reads; the
            // single-column TimeOfCheckUTC index covers cleanup.
            modelBuilder.Entity<HistoricalStatusData>()
             .HasIndex(x => new { x.StatusCheckId, x.TimeOfCheckUTC })
             .IsDescending(false, true)
             .HasDatabaseName("IX_HistoricalStatusDataSet_StatusCheckId_TimeOfCheckUTC");

            modelBuilder.Entity<HistoricalStatusData>()
             .HasIndex(x => x.TimeOfCheckUTC)
             .HasDatabaseName("IX_HistoricalStatusDataSet_TimeOfCheckUTC");

            // Issue #107: WebhookExecutionLogSet — audit trail of outbound
            // webhook attempts. Two FK behaviours: StatusCheck cascade
            // (deleting a check removes its log rows), HistoricalStatusData
            // SET NULL so retention cleanup of the parent tick doesn't
            // orphan the log entry.
            modelBuilder.Entity<WebhookExecutionLog>()
             .HasOne(x => x.StatusCheck)
             .WithMany()
             .HasForeignKey(x => x.StatusCheckId)
             .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<WebhookExecutionLog>()
             .HasOne(x => x.HistoricalStatusData)
             .WithMany()
             .HasForeignKey(x => x.HistoricalStatusDataId)
             .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<WebhookExecutionLog>()
             .Property(x => x.TargetUrl)
             .IsRequired();

            modelBuilder.Entity<WebhookExecutionLog>()
             .HasIndex(x => new { x.StatusCheckId, x.AttemptedUtc })
             .IsDescending(false, true)
             .HasDatabaseName("IX_WebhookExecutionLogSet_StatusCheckId_AttemptedUtc");

            modelBuilder.Entity<WebhookExecutionLog>()
             .HasIndex(x => x.AttemptedUtc)
             .IsDescending(true)
             .HasDatabaseName("IX_WebhookExecutionLogSet_AttemptedUtc");

            modelBuilder.Entity<WebhookExecutionLog>()
             .HasIndex(x => x.Outcome)
             .HasDatabaseName("IX_WebhookExecutionLogSet_Outcome");

            // Issue #241/#253: alert audit log — same FK/index shape as the
            // webhook log. Cascade on check delete; newest-first indexes.
            modelBuilder.Entity<AlertDeliveryLog>()
             .HasOne(x => x.StatusCheck)
             .WithMany()
             .HasForeignKey(x => x.StatusCheckId)
             .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AlertDeliveryLog>()
             .HasIndex(x => new { x.StatusCheckId, x.AttemptedUtc })
             .IsDescending(false, true)
             .HasDatabaseName("IX_AlertDeliveryLogSet_StatusCheckId_AttemptedUtc");

            modelBuilder.Entity<AlertDeliveryLog>()
             .HasIndex(x => x.AttemptedUtc)
             .IsDescending(true)
             .HasDatabaseName("IX_AlertDeliveryLogSet_AttemptedUtc");

            modelBuilder.Entity<AlertDeliveryLog>()
             .HasIndex(x => x.Outcome)
             .HasDatabaseName("IX_AlertDeliveryLogSet_Outcome");

            // Issue #138: persisted daily rollup. Cascade with its check; one
            // row per (check, day) — unique so the rollup upsert can't double.
            modelBuilder.Entity<DailyStatusRollup>()
             .HasOne(x => x.StatusCheck)
             .WithMany()
             .HasForeignKey(x => x.StatusCheckId)
             .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<DailyStatusRollup>()
             .HasIndex(x => new { x.StatusCheckId, x.Day })
             .IsUnique()
             .HasDatabaseName("IX_DailyStatusRollupSet_StatusCheckId_Day");

            // Issue #167: enforce the SiteSettings singleton at the persistence
            // boundary — a CHECK ("Id" = 1) makes a second settings row
            // impossible even via a direct relational insert. (Quoted so the
            // case-sensitive Postgres column matches; SQLite honours it too.)
            modelBuilder.Entity<SiteSettings>()
             .ToTable(t => t.HasCheckConstraint("CK_SiteSettingsSet_Singleton", "\"Id\" = 1"));

            // Issue #249: update checks default ON. A database default of true means
            // an existing settings row gets opted in when this column is added on
            // upgrade (not left off by the column's implicit false).
            modelBuilder.Entity<SiteSettings>()
             .Property(s => s.UpdateCheckEnabled)
             .HasDefaultValue(true);

            // Issue #168: at most one OPEN auto-generated incident per source
            // check — a partial unique index so concurrent scheduler ticks can't
            // draft duplicates (the service also queries-before-insert and treats
            // the unique violation as "already drafted"). Manual incidents (null
            // source) and resolved ones are excluded. Quoted identifiers match
            // Postgres; SQLite honours the same partial-index syntax for tests.
            modelBuilder.Entity<Incident>()
             .HasIndex(x => x.SourceStatusCheckId)
             .IsUnique()
             .HasFilter("\"SourceStatusCheckId\" IS NOT NULL AND NOT \"Resolved\" AND \"AuotmaticallyGeneratedReport\"")
             .HasDatabaseName("IX_IncidentSet_SourceStatusCheckId_OpenAuto");

            // Issue #241 Phase C: Web Push subscriptions. Endpoint is the natural key —
            // unique so a re-subscribe with the same browser endpoint upserts rather
            // than duplicating.
            modelBuilder.Entity<PushSubscription>()
             .HasIndex(x => x.Endpoint)
             .IsUnique()
             .HasDatabaseName("IX_PushSubscriptionSet_Endpoint");

            base.OnModelCreating(modelBuilder);
        }
        public DbSet<StatusCheck> StatusCheckSet { get; set; }
        public DbSet<HistoricalStatusData> HistoricalStatusDataSet { get; set; }
        public DbSet<HistoricalStatusAction> HistoricalStatusActionSet { get; set; }
        public DbSet<WebhookExecutionLog> WebhookExecutionLogSet { get; set; }
        public DbSet<AlertDeliveryLog> AlertDeliveryLogSet { get; set; }

        public DbSet<Incident> IncidentSet { get; set; }
        public DbSet<DailyStatusRollup> DailyStatusRollupSet { get; set; }
        public DbSet<RollupMaintenanceState> RollupMaintenanceStateSet { get; set; }
        public DbSet<SiteSettings> SiteSettingsSet { get; set; }
        public DbSet<PushSubscription> PushSubscriptionSet { get; set; }
    }
}
