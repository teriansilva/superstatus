using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class RetypeAlertLogChannelToTypeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #343 Phase 5: replace the fixed AlertChannel enum column ("Channel", integer)
            // with a channel-typeid string ("ChannelTypeId", text) so arbitrary channels
            // (slack / discord / telegram / …) log correctly. Non-destructive: add the new
            // column, backfill 1:1 from the enum (0→email / 1→webpush / 2→webhook), then drop
            // the old one. PG-only (the app runs on Postgres); the temporary default lets the
            // NOT NULL add succeed on existing rows and is dropped to match the model.
            migrationBuilder.Sql(
                """
                ALTER TABLE "AlertDeliveryLogSet"
                    ADD COLUMN "ChannelTypeId" text NOT NULL DEFAULT 'email';

                UPDATE "AlertDeliveryLogSet"
                SET "ChannelTypeId" = CASE "Channel"
                    WHEN 0 THEN 'email'
                    WHEN 1 THEN 'webpush'
                    WHEN 2 THEN 'webhook'
                    ELSE 'email'
                END;

                ALTER TABLE "AlertDeliveryLogSet" ALTER COLUMN "ChannelTypeId" DROP DEFAULT;
                ALTER TABLE "AlertDeliveryLogSet" DROP COLUMN "Channel";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: re-add the enum column and map the known type ids back. Channels that
            // never existed pre-Phase-5 (slack/discord/telegram) can't be represented by the
            // old enum, so they collapse to 0 (email) — a best-effort reverse, acceptable
            // because those rows can't predate this migration.
            migrationBuilder.Sql(
                """
                ALTER TABLE "AlertDeliveryLogSet"
                    ADD COLUMN "Channel" integer NOT NULL DEFAULT 0;

                UPDATE "AlertDeliveryLogSet"
                SET "Channel" = CASE "ChannelTypeId"
                    WHEN 'email' THEN 0
                    WHEN 'webpush' THEN 1
                    WHEN 'webhook' THEN 2
                    ELSE 0
                END;

                ALTER TABLE "AlertDeliveryLogSet" ALTER COLUMN "Channel" DROP DEFAULT;
                ALTER TABLE "AlertDeliveryLogSet" DROP COLUMN "ChannelTypeId";
                """);
        }
    }
}
