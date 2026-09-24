using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddIncidentSeverityAndResolvedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                table: "IncidentSet",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Severity",
                table: "IncidentSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                table: "IncidentSet");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "IncidentSet");
        }
    }
}
