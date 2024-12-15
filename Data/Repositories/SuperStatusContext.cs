using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public class SuperStatusContext(DbContextOptions<SuperStatusContext> options) : DbContext(options)
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

        //TODO: StatusCheck Data should be stored in the database
        //public DbSet<StatusCheck> StatusCheckSet { get; set; }
        public DbSet<HistoricalStatusData> HistoricalStatusDataSet { get; set; }
        public DbSet<HistoricalStatusAction> HistoricalStatusActionSet { get; set; }
    }
}
