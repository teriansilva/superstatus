using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddAutoUpdateSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoUpdateEnabled",
                table: "SiteSettingsSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoUpdateLastRunUtc",
                table: "SiteSettingsSet",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "AutoUpdateTimeUtc",
                table: "SiteSettingsSet",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(3, 0, 0));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoUpdateEnabled",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "AutoUpdateLastRunUtc",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "AutoUpdateTimeUtc",
                table: "SiteSettingsSet");
        }
    }
}
