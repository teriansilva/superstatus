using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddHistoricalStatusDataIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HistoricalStatusDataSet_StatusCheckId",
                table: "HistoricalStatusDataSet");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalStatusDataSet_StatusCheckId_TimeOfCheckUTC",
                table: "HistoricalStatusDataSet",
                columns: new[] { "StatusCheckId", "TimeOfCheckUTC" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalStatusDataSet_TimeOfCheckUTC",
                table: "HistoricalStatusDataSet",
                column: "TimeOfCheckUTC");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HistoricalStatusDataSet_StatusCheckId_TimeOfCheckUTC",
                table: "HistoricalStatusDataSet");

            migrationBuilder.DropIndex(
                name: "IX_HistoricalStatusDataSet_TimeOfCheckUTC",
                table: "HistoricalStatusDataSet");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalStatusDataSet_StatusCheckId",
                table: "HistoricalStatusDataSet",
                column: "StatusCheckId");
        }
    }
}
