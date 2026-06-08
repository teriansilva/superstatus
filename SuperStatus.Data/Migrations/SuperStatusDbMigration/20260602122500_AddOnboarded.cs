using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddOnboarded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OnboardedUtc",
                table: "SiteSettingsSet",
                type: "timestamp with time zone",
                nullable: true);

            // #181: an existing install already has its settings row — treat it as
            // already-onboarded so the upgrade doesn't trap operators in the
            // first-run wizard. A genuinely fresh install has no row yet; its
            // later-seeded row keeps OnboardedUtc NULL → the wizard runs.
            migrationBuilder.Sql(
                "UPDATE \"SiteSettingsSet\" SET \"OnboardedUtc\" = NOW() WHERE \"OnboardedUtc\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnboardedUtc",
                table: "SiteSettingsSet");
        }
    }
}
