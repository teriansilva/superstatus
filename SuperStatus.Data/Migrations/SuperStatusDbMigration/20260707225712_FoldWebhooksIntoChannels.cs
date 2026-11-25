using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class FoldWebhooksIntoChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #343 Phase 4: fold the standalone per-check webhooks into the notification
            // channel model. For each Webhook, create a webhook-only AlertProfile with a
            // 'webhook' AlertProfileChannel (ConfigJson carries the URL); for each
            // StatusCheckWebhook link, link that check to the new profile, preserving the
            // throttle anchor (LastFiredUtc -> the profile-link's AlertLastFiredUtc). The
            // legacy Webhook / StatusCheckWebhook / WebhookExecutionLog tables are RETAINED
            // (deprecated) so this is non-destructive; a later explicit migration drops
            // them. PG-only data backfill (the app runs on Postgres); a no-op on an empty
            // WebhookSet.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    w RECORD;
                    pid bigint;
                BEGIN
                    FOR w IN SELECT * FROM "WebhookSet" LOOP
                        INSERT INTO "AlertProfileSet"
                            ("Name","EmailEnabled","EmailRecipients","UsesSiteDefaultRecipients","WebPushEnabled","CreatedUtc")
                        VALUES (w."Name", false, '', false, false, now())
                        RETURNING "Id" INTO pid;

                        INSERT INTO "AlertProfileChannelSet"
                            ("AlertProfileId","ProviderType","IsEnabled","ConfigJson")
                        VALUES (pid, 'webhook', w."IsEnabled",
                                json_build_object('url', w."Url")::text);

                        INSERT INTO "StatusCheckAlertProfileSet"
                            ("StatusCheckId","AlertProfileId","AlertedOutageDownSinceUtc","AlertLastFiredUtc")
                        SELECT scw."StatusCheckId", pid, NULL, scw."LastFiredUtc"
                        FROM "StatusCheckWebhookSet" scw
                        WHERE scw."WebhookId" = w."Id";
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only data backfill: the Up adds derived profiles/channels/links from
            // the (retained) legacy webhook tables and changes no schema. A precise reverse
            // can't distinguish the derived AlertProfiles from operator-created ones, so
            // Down is intentionally a no-op — the legacy webhook tables are retained, so the
            // pre-fold state is still fully described by them.
        }
    }
}
