using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddFooterSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #170: backfill the existing singleton row so the footer is visually
            // unchanged on first deploy — the prior static classification text, an
            // empty link list, and the Admin link kept visible (default on).
            migrationBuilder.AddColumn<string>(
                name: "FooterLinksJson",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "FooterText",
                table: "SiteSettingsSet",
                type: "text",
                nullable: false,
                defaultValue: "UNCLASSIFIED // INTERNAL USE");

            migrationBuilder.AddColumn<bool>(
                name: "ShowAdminLink",
                table: "SiteSettingsSet",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FooterLinksJson",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "FooterText",
                table: "SiteSettingsSet");

            migrationBuilder.DropColumn(
                name: "ShowAdminLink",
                table: "SiteSettingsSet");
        }
    }
}
