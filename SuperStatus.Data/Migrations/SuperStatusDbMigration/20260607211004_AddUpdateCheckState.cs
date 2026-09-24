using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddUpdateCheckState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UpdateCheckEnabled",
                table: "SiteSettingsSet",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateLastCheckError",
                table: "SiteSettingsSet",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdateLastCheckedUtc",
                table: "SiteSettingsSet",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateLatestNotesUrl",
                table: "SiteSettingsSet",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateLatestVersion",
                table: "SiteSettingsSet",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateCheckEnabled",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "UpdateLastCheckError",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "UpdateLastCheckedUtc",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "UpdateLatestNotesUrl",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "UpdateLatestVersion",
                table: "SiteSettingsSet");
        }
    }
}
