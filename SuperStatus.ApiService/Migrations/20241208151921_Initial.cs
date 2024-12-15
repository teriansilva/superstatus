using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistoricalStatusDataSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusCheckId = table.Column<long>(type: "INTEGER", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseTimeInMs = table.Column<long>(type: "INTEGER", nullable: false),
                    TimeOfCheckUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CheckFailed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalStatusDataSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HistoricalStatusActionSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusCheckId = table.Column<long>(type: "INTEGER", nullable: false),
                    HistoricalStatusDataId = table.Column<long>(type: "INTEGER", nullable: false),
                    ActionType = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeOfExecutionUTC = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalStatusActionSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricalStatusActionSet_HistoricalStatusDataSet_HistoricalStatusDataId",
                        column: x => x.HistoricalStatusDataId,
                        principalTable: "HistoricalStatusDataSet",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalStatusActionSet_HistoricalStatusDataId",
                table: "HistoricalStatusActionSet",
                column: "HistoricalStatusDataId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricalStatusActionSet");

            migrationBuilder.DropTable(
                name: "HistoricalStatusDataSet");
        }
    }
}
