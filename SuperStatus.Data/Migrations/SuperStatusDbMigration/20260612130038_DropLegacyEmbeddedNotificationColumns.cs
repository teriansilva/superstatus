using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <summary>
    /// Issue #291 Phase D / #293 Phase C: drop the legacy embedded
    /// notification + threshold columns from StatusCheckSet. DATA LOSS IS THE
    /// POINT — the data lives in the linked Webhook / AlertProfile / Sla
    /// entities since the #291-A/#293-A backfills.
    ///
    /// UPGRADE EDGE (multi-version jump): an instance that skips the releases
    /// where the C# startup backfills ran arrives here with legacy config
    /// that was never entity-translated — and the reduced startup backfill
    /// can no longer translate it (the entity properties are gone). So the
    /// translation runs HERE, in raw SQL, immediately before the drop,
    /// mirroring the (now deleted) C# normalization semantics:
    ///   - webhooks dedupe on (Url, ThrottleMinutes); auto-name = URL host,
    ///     '#N'-suffixed on collision; link anchor = most recent action time.
    ///   - alert profiles dedupe on (canonical recipient set, EmailEnabled,
    ///     WebPushEnabled, UsesSiteDefaultRecipients); canonical = split on
    ///     comma/semicolon/whitespace, trim, lower, distinct, sort; email-on
    ///     with no recipients = the site-default profile; link anchors carried
    ///     from the check's episode/throttle columns.
    ///   - SLAs dedupe on (SlowThresholdMs, Target 100, Critical 100); ms
    ///     floored at 1; the first SLA created becomes the default when none
    ///     exists; names 'Default' / 'Legacy N ms', '#N'-suffixed.
    /// Already-linked checks are skipped per family, so on an instance that
    /// ran the C# backfills this SQL finds nothing to do (idempotent).
    ///
    /// The SQL is PostgreSQL-only (plpgsql) and guarded by ActiveProvider:
    /// Sqlite (the test provider) cannot run it — and the HISTORIC chain
    /// already contains PG-only SQL (NOW(), ::int casts), so a full Sqlite
    /// Database.Migrate() was never possible. Judgment call, documented:
    /// the structural test asserts the SQL precedes the drops + the exact
    /// column set; the translation semantics were covered by the Phase A
    /// suites while the C# path existed, and the SQL mirrors them.
    /// </summary>
    public partial class DropLegacyEmbeddedNotificationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql") == true)
            {
                // Canonical recipient set, identical to the deleted
                // LinkedTargetNormalizationService.NormalizeRecipients:
                // split on , ; whitespace → trim → lower → distinct → sort → join ','.
                migrationBuilder.Sql("""
                    CREATE FUNCTION pg_temp.ss_canon_recipients(raw text) RETURNS text
                    LANGUAGE sql AS $fn$
                        SELECT COALESCE(string_agg(DISTINCT t, ',' ORDER BY t), '')
                        FROM (
                            SELECT lower(btrim(u)) AS t
                            FROM unnest(regexp_split_to_array(COALESCE(raw, ''), '[,;[:space:]]+')) AS u
                            WHERE btrim(u) <> ''
                        ) s
                    $fn$;
                    """);

                migrationBuilder.Sql("""
                    DO $$
                    DECLARE
                        rec record;
                        target_id bigint;
                        base_name text;
                        candidate text;
                        n int;
                        canon text;
                        uses_default boolean;
                        has_default boolean;
                    BEGIN
                        -- ============================================================
                        -- 1. Legacy webhook fields → WebhookSet + StatusCheckWebhookSet.
                        --    Only checks with the webhook ENABLED and a non-empty URL
                        --    ever fired; checks that already have webhook links were
                        --    translated by the C# backfill — skipped (per family).
                        -- ============================================================
                        FOR rec IN
                            SELECT sc."Id",
                                   btrim(sc."WebHookOnErrorUrl") AS url,
                                   GREATEST(sc."ThrottleWebHookToExecuteOnlyEveryXMinutes", 0) AS throttle
                            FROM "StatusCheckSet" sc
                            WHERE sc."IsWebHookOnErrorEnabled"
                              AND btrim(COALESCE(sc."WebHookOnErrorUrl", '')) <> ''
                              AND NOT EXISTS (SELECT 1 FROM "StatusCheckWebhookSet" l WHERE l."StatusCheckId" = sc."Id")
                            ORDER BY sc."Id"
                        LOOP
                            -- Dedupe identity: (Url, ThrottleMinutes) — same URL with a
                            -- different throttle is a different reusable target.
                            SELECT w."Id" INTO target_id
                            FROM "WebhookSet" w
                            WHERE w."Url" = rec.url AND w."ThrottleMinutes" = rec.throttle
                            ORDER BY w."Id" LIMIT 1;

                            IF target_id IS NULL THEN
                                -- Auto-name from the URL host (fallback: the URL itself),
                                -- '#N'-suffixed on collision — the AutoWebhookName rule.
                                base_name := COALESCE(substring(rec.url from '^[A-Za-z][A-Za-z0-9+.-]*://([^/:?#]+)'), rec.url);
                                candidate := base_name; n := 2;
                                WHILE EXISTS (SELECT 1 FROM "WebhookSet" w WHERE w."Name" = candidate) LOOP
                                    candidate := base_name || ' #' || n; n := n + 1;
                                END LOOP;
                                INSERT INTO "WebhookSet" ("Name", "Url", "IsEnabled", "ThrottleMinutes", "CreatedUtc")
                                VALUES (candidate, rec.url, TRUE, rec.throttle, now())
                                RETURNING "Id" INTO target_id;
                            END IF;

                            -- Throttle anchor = the most recent action time, so a check
                            -- inside its legacy throttle window can't double-fire after
                            -- the upgrade (mirrors the C# backfill's anchor carry).
                            INSERT INTO "StatusCheckWebhookSet" ("StatusCheckId", "WebhookId", "LastFiredUtc")
                            VALUES (rec."Id", target_id,
                                    (SELECT max(a."TimeOfExecutionUTC")
                                     FROM "HistoricalStatusActionSet" a
                                     WHERE a."StatusCheckId" = rec."Id"));
                        END LOOP;

                        -- ============================================================
                        -- 2. Legacy alert fields → AlertProfileSet + StatusCheckAlertProfileSet.
                        -- ============================================================
                        FOR rec IN
                            SELECT sc."Id",
                                   sc."EmailAlertsEnabled"        AS email_on,
                                   sc."WebPushAlertsEnabled"      AS push_on,
                                   sc."EmailRecipients"           AS raw_recipients,
                                   sc."AlertedOutageDownSinceUtc" AS episode_anchor,
                                   sc."AlertLastFiredUtc"         AS fired_anchor
                            FROM "StatusCheckSet" sc
                            WHERE (sc."EmailAlertsEnabled" OR sc."WebPushAlertsEnabled")
                              AND NOT EXISTS (SELECT 1 FROM "StatusCheckAlertProfileSet" l WHERE l."StatusCheckId" = sc."Id")
                            ORDER BY sc."Id"
                        LOOP
                            canon := CASE WHEN rec.email_on THEN pg_temp.ss_canon_recipients(rec.raw_recipients) ELSE '' END;
                            -- Legacy semantics: email on + empty recipients fell back to
                            -- the site default at send time → the explicit site-default profile.
                            uses_default := rec.email_on AND canon = '';

                            -- Dedupe identity includes the channel flags: an admin-made
                            -- MUTED profile with the same recipients must not capture a
                            -- legacy email-enabled check (the #291 Hermes lesson).
                            SELECT p."Id" INTO target_id
                            FROM "AlertProfileSet" p
                            WHERE pg_temp.ss_canon_recipients(p."EmailRecipients") = canon
                              AND p."EmailEnabled" = rec.email_on
                              AND p."WebPushEnabled" = rec.push_on
                              AND p."UsesSiteDefaultRecipients" = uses_default
                            ORDER BY p."Id" LIMIT 1;

                            IF target_id IS NULL THEN
                                -- AutoProfileName: 'Default recipients' / first recipient
                                -- (+N) / 'Web push', '#N'-suffixed on collision.
                                base_name := CASE
                                    WHEN uses_default THEN 'Default recipients'
                                    WHEN canon <> '' THEN
                                        CASE WHEN position(',' in canon) = 0 THEN canon
                                             ELSE split_part(canon, ',', 1) || ' +' ||
                                                  (array_length(string_to_array(canon, ','), 1) - 1)
                                        END
                                    ELSE 'Web push'
                                END;
                                candidate := base_name; n := 2;
                                WHILE EXISTS (SELECT 1 FROM "AlertProfileSet" p WHERE p."Name" = candidate) LOOP
                                    candidate := base_name || ' #' || n; n := n + 1;
                                END LOOP;
                                INSERT INTO "AlertProfileSet" ("Name", "EmailEnabled", "EmailRecipients", "UsesSiteDefaultRecipients", "WebPushEnabled", "CreatedUtc")
                                VALUES (candidate, rec.email_on, canon, uses_default, rec.push_on, now())
                                RETURNING "Id" INTO target_id;
                            END IF;

                            -- Anchors carried from the check's legacy episode/throttle
                            -- columns so an already-alerted outage doesn't re-fire.
                            INSERT INTO "StatusCheckAlertProfileSet" ("StatusCheckId", "AlertProfileId", "AlertedOutageDownSinceUtc", "AlertLastFiredUtc")
                            VALUES (rec."Id", target_id, rec.episode_anchor, rec.fired_anchor);
                        END LOOP;

                        -- ============================================================
                        -- 3. Legacy ExpectedResponseTimeInMs → SlaSet + StatusCheckSet.SlaId.
                        -- ============================================================
                        has_default := EXISTS (SELECT 1 FROM "SlaSet" s WHERE s."IsDefault");
                        FOR rec IN
                            SELECT sc."Id", GREATEST(sc."ExpectedResponseTimeInMs", 1) AS ms
                            FROM "StatusCheckSet" sc
                            WHERE sc."SlaId" IS NULL
                            ORDER BY sc."Id"
                        LOOP
                            -- Legacy translation identity: same threshold AND the
                            -- behavior-identical 100/100 targets — an admin-tuned SLA
                            -- that merely shares the threshold is never captured.
                            SELECT s."Id" INTO target_id
                            FROM "SlaSet" s
                            WHERE s."SlowThresholdMs" = rec.ms
                              AND s."TargetUptimePercent" = 100
                              AND s."CriticalUptimePercent" = 100
                            ORDER BY s."Id" LIMIT 1;

                            IF target_id IS NULL THEN
                                -- The first SLA created becomes the default when none exists.
                                base_name := CASE WHEN NOT has_default THEN 'Default' ELSE 'Legacy ' || rec.ms || ' ms' END;
                                candidate := base_name; n := 2;
                                WHILE EXISTS (SELECT 1 FROM "SlaSet" s WHERE s."Name" = candidate) LOOP
                                    candidate := base_name || ' #' || n; n := n + 1;
                                END LOOP;
                                INSERT INTO "SlaSet" ("Name", "TargetUptimePercent", "CriticalUptimePercent", "SlowThresholdMs", "IsDefault", "CreatedUtc")
                                VALUES (candidate, 100, 100, rec.ms, NOT has_default, now())
                                RETURNING "Id" INTO target_id;
                                has_default := TRUE;
                            END IF;

                            UPDATE "StatusCheckSet" SET "SlaId" = target_id WHERE "Id" = rec."Id";
                        END LOOP;
                    END $$;
                    """);

                migrationBuilder.Sql("DROP FUNCTION pg_temp.ss_canon_recipients(text);");
            }

            migrationBuilder.DropColumn(
                name: "EmailAlertsEnabled",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "EmailRecipients",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "ExpectedResponseTimeInMs",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "IsWebHookOnErrorEnabled",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "ThrottleWebHookToExecuteOnlyEveryXMinutes",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "WebHookOnErrorUrl",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "WebPushAlertsEnabled",
                table: "StatusCheckSet");
        }

        /// <inheritdoc />
        /// <remarks>Schema-only rollback: the dropped values are NOT
        /// recoverable (they live in the linked entities). Columns come back
        /// with their defaults; the app reads only the links anyway.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailAlertsEnabled",
                table: "StatusCheckSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailRecipients",
                table: "StatusCheckSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "ExpectedResponseTimeInMs",
                table: "StatusCheckSet",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsWebHookOnErrorEnabled",
                table: "StatusCheckSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ThrottleWebHookToExecuteOnlyEveryXMinutes",
                table: "StatusCheckSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WebHookOnErrorUrl",
                table: "StatusCheckSet",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "WebPushAlertsEnabled",
                table: "StatusCheckSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
