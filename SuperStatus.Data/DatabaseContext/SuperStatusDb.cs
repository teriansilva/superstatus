using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.DatabaseContext
{
    public class SuperStatusDb(DbContextOptions<SuperStatusDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {

            modelBuilder.Entity<HistoricalStatusData>()
             .HasOne(x => x.HistoricalStatusAction)
             .WithOne(y => y.HistoricalStatusData)
             .HasForeignKey<HistoricalStatusAction>(y => y.HistoricalStatusDataId)
             .OnDelete(DeleteBehavior.ClientCascade);

            base.OnModelCreating(modelBuilder);
        }
        public DbSet<StatusCheck> StatusCheckSet { get; set; }
        public DbSet<HistoricalStatusData> HistoricalStatusDataSet { get; set; }
        public DbSet<HistoricalStatusAction> HistoricalStatusActionSet { get; set; }
        public DbSet<Incident> IncidentSet { get; set; }
        public DbSet<Configuration> ConfigurationSet { get; set; }
    }
}
