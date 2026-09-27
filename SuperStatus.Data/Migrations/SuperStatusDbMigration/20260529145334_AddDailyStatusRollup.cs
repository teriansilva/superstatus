using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddDailyStatusRollup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyStatusRollupSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatusCheckId = table.Column<long>(type: "bigint", nullable: false),
                    Day = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Down = table.Column<int>(type: "integer", nullable: false),
                    Degraded = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyStatusRollupSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyStatusRollupSet_StatusCheckSet_StatusCheckId",
                        column: x => x.StatusCheckId,
                        principalTable: "StatusCheckSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyStatusRollupSet_StatusCheckId_Day",
                table: "DailyStatusRollupSet",
                columns: new[] { "StatusCheckId", "Day" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyStatusRollupSet");
        }
    }
}
