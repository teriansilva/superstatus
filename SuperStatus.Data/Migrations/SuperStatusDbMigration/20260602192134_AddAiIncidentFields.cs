using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddAiIncidentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoIncidentEnabled",
                table: "StatusCheckSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DownSinceUtc",
                table: "StatusCheckSet",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiApiKey",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AiBaseUrl",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AiEnabled",
                table: "SiteSettingsSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiModel",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            // #168: the column-add backfills existing rows to 0; SiteSettingsService
            // coerces a non-positive timeout/threshold to the documented defaults on
            // read (20s / 5 min), and new EF-inserted rows carry the entity initializer
            // values — so 0 here is a harmless legacy backfill, no store default needed.
            migrationBuilder.AddColumn<int>(
                name: "AiTimeoutSeconds",
                table: "SiteSettingsSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AutoIncidentThresholdMinutes",
                table: "SiteSettingsSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "SourceStatusCheckId",
                table: "IncidentSet",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentSet_SourceStatusCheckId_OpenAuto",
                table: "IncidentSet",
                column: "SourceStatusCheckId",
                unique: true,
                filter: "\"SourceStatusCheckId\" IS NOT NULL AND NOT \"Resolved\" AND \"AuotmaticallyGeneratedReport\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IncidentSet_SourceStatusCheckId_OpenAuto",
                table: "IncidentSet");

            migrationBuilder.DropColumn(
                name: "AutoIncidentEnabled",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "DownSinceUtc",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "AiApiKey",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "AiBaseUrl",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "AiEnabled",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "AiModel",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "AiTimeoutSeconds",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "AutoIncidentThresholdMinutes",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SourceStatusCheckId",
                table: "IncidentSet");
        }
    }
}
