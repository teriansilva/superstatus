using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddAlertProfileChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertProfileChannelSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertProfileId = table.Column<long>(type: "bigint", nullable: false),
                    ProviderType = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertProfileChannelSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertProfileChannelSet_AlertProfileSet_AlertProfileId",
                        column: x => x.AlertProfileId,
                        principalTable: "AlertProfileSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertProfileChannelSet_AlertProfileId_ProviderType",
                table: "AlertProfileChannelSet",
                columns: new[] { "AlertProfileId", "ProviderType" },
                unique: true);

            // #343 Phase 3: behavior-preserving backfill. Every existing profile gets an
            // "email" and a "webpush" channel row mirroring the (now deprecated) boolean
            // columns; the email row carries the recipients + site-default flag as its
            // config, in the exact shape EmailChannelSettings serializes
            // ({"recipients":"…","usesSiteDefault":true|false}). The legacy columns are
            // kept (this migration is non-destructive); a later explicit migration drops
            // them. PG-only (the app runs on Postgres); the raw SQL is a no-op on an empty
            // AlertProfileSet. Guarded to no-op if re-run (unique index) is unnecessary —
            // the table was just created empty.
            migrationBuilder.Sql(
                """
                INSERT INTO "AlertProfileChannelSet" ("AlertProfileId", "ProviderType", "IsEnabled", "ConfigJson")
                SELECT p."Id", 'email', p."EmailEnabled",
                       json_build_object('recipients', p."EmailRecipients", 'usesSiteDefault', p."UsesSiteDefaultRecipients")::text
                FROM "AlertProfileSet" p;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO "AlertProfileChannelSet" ("AlertProfileId", "ProviderType", "IsEnabled", "ConfigJson")
                SELECT p."Id", 'webpush', p."WebPushEnabled", NULL
                FROM "AlertProfileSet" p;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertProfileChannelSet");
        }
    }
}
