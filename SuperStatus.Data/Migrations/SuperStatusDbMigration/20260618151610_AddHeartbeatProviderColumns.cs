using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddHeartbeatProviderColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeartbeatToken",
                table: "StatusCheckSet",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatUtc",
                table: "StatusCheckSet",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusCheckSet_HeartbeatToken",
                table: "StatusCheckSet",
                column: "HeartbeatToken",
                unique: true,
                filter: "\"HeartbeatToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StatusCheckSet_HeartbeatToken",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "HeartbeatToken",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatUtc",
                table: "StatusCheckSet");
        }
    }
}
