using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddStatusCheckIntervalSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #82: backfill existing rows (and the column default for any raw
            // insert) to 10 — the old global JobIntervallInSeconds — so polling
            // cadence is unchanged on upgrade. New checks created via the admin
            // form default to 60 and are clamped to 5–3600 (StatusCheckSchedule).
            migrationBuilder.AddColumn<int>(
                name: "IntervalSeconds",
                table: "StatusCheckSet",
                type: "integer",
                nullable: false,
                defaultValue: 10);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntervalSeconds",
                table: "StatusCheckSet");
        }
    }
}
