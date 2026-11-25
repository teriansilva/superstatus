using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddCheckProviderColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Epic #271 / #312 Phase 1: pluggable check providers.
            migrationBuilder.AddColumn<string>(
                name: "ConfigJson",
                table: "StatusCheckSet",
                type: "text",
                nullable: true);

            // Every existing row is an HTTP check — backfill to the "http" provider.
            // The column keeps a DB default of "http" as a harmless safety net; new
            // rows always set it via the app (AddOrUpdateStatusCheck).
            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "StatusCheckSet",
                type: "text",
                nullable: false,
                defaultValue: "http");

            migrationBuilder.AddColumn<string>(
                name: "MetricsJson",
                table: "HistoricalStatusDataSet",
                type: "text",
                nullable: true);

            // Backfill ConfigJson from the legacy HTTP columns so every existing row
            // carries explicit, schema-versioned provider config (matches what the
            // edit dialog round-trips). Postgres-only (json_build_object); other
            // providers (e.g. the Sqlite test path) leave ConfigJson null, which the
            // runtime resolves from the same columns via the behavior-preserving
            // fallback in StatusCheckService.ResolveProbe — so behavior is identical
            // either way.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql") == true)
            {
                migrationBuilder.Sql("""
                    UPDATE "StatusCheckSet"
                    SET "ConfigJson" = json_build_object(
                        'schemaVersion', 1,
                        'url', COALESCE("StatusCheckUrl", ''),
                        'expectedStatusCode', "ExpectedStatusCode"
                    )::text
                    WHERE "ConfigJson" IS NULL;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfigJson",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "MetricsJson",
                table: "HistoricalStatusDataSet");
        }
    }
}
