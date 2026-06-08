using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddSmtpSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlertDefaultRecipients",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SmtpFromAddress",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SmtpFromName",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SmtpHost",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SmtpPassword",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SmtpPort",
                table: "SiteSettingsSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SmtpUseStartTls",
                table: "SiteSettingsSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SmtpUsername",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SmtpVerifiedUtc",
                table: "SiteSettingsSet",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlertDefaultRecipients",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SmtpFromAddress",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SmtpFromName",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SmtpHost",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SmtpPassword",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SmtpPort",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SmtpUseStartTls",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SmtpUsername",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "SmtpVerifiedUtc",
                table: "SiteSettingsSet");
        }
    }
}
