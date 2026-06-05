using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class ChangeHistoricalStatusActionCascadeToDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HistoricalStatusActionSet_HistoricalStatusDataSet_Historica~",
                table: "HistoricalStatusActionSet");

            migrationBuilder.AddForeignKey(
                name: "FK_HistoricalStatusActionSet_HistoricalStatusDataSet_Historica~",
                table: "HistoricalStatusActionSet",
                column: "HistoricalStatusDataId",
                principalTable: "HistoricalStatusDataSet",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HistoricalStatusActionSet_HistoricalStatusDataSet_Historica~",
                table: "HistoricalStatusActionSet");

            migrationBuilder.AddForeignKey(
                name: "FK_HistoricalStatusActionSet_HistoricalStatusDataSet_Historica~",
                table: "HistoricalStatusActionSet",
                column: "HistoricalStatusDataId",
                principalTable: "HistoricalStatusDataSet",
                principalColumn: "Id");
        }
    }
}
