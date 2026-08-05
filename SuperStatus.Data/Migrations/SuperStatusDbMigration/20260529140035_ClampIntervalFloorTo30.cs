using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class ClampIntervalFloorTo30 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #136: the polling floor rose from 5 s to 30 s. Bump legacy rows
            // stored below 30 (e.g. the 10 s backfill from #82) up to the floor,
            // so the stored cadence matches what the scheduler now enforces and
            // the admin UI shows the real interval. Data-only; no schema change.
            migrationBuilder.Sql("UPDATE \"StatusCheckSet\" SET \"IntervalSeconds\" = 30 WHERE \"IntervalSeconds\" < 30;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
