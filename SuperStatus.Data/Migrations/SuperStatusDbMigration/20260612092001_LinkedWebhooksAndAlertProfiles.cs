using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class LinkedWebhooksAndAlertProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "WebhookId",
                table: "WebhookExecutionLogSet",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AlertProfileId",
                table: "AlertDeliveryLogSet",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertProfileSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    EmailRecipients = table.Column<string>(type: "text", nullable: false),
                    UsesSiteDefaultRecipients = table.Column<bool>(type: "boolean", nullable: false),
                    WebPushEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertProfileSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackfillReportSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SummaryJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackfillReportSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ThrottleMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StatusCheckAlertProfileSet",
                columns: table => new
                {
                    StatusCheckId = table.Column<long>(type: "bigint", nullable: false),
                    AlertProfileId = table.Column<long>(type: "bigint", nullable: false),
                    AlertedOutageDownSinceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AlertLastFiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusCheckAlertProfileSet", x => new { x.StatusCheckId, x.AlertProfileId });
                    table.ForeignKey(
                        name: "FK_StatusCheckAlertProfileSet_AlertProfileSet_AlertProfileId",
                        column: x => x.AlertProfileId,
                        principalTable: "AlertProfileSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StatusCheckAlertProfileSet_StatusCheckSet_StatusCheckId",
                        column: x => x.StatusCheckId,
                        principalTable: "StatusCheckSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusCheckWebhookSet",
                columns: table => new
                {
                    StatusCheckId = table.Column<long>(type: "bigint", nullable: false),
                    WebhookId = table.Column<long>(type: "bigint", nullable: false),
                    LastFiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusCheckWebhookSet", x => new { x.StatusCheckId, x.WebhookId });
                    table.ForeignKey(
                        name: "FK_StatusCheckWebhookSet_StatusCheckSet_StatusCheckId",
                        column: x => x.StatusCheckId,
                        principalTable: "StatusCheckSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StatusCheckWebhookSet_WebhookSet_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "WebhookSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookExecutionLogSet_WebhookId",
                table: "WebhookExecutionLogSet",
                column: "WebhookId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveryLogSet_AlertProfileId",
                table: "AlertDeliveryLogSet",
                column: "AlertProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusCheckAlertProfileSet_AlertProfileId",
                table: "StatusCheckAlertProfileSet",
                column: "AlertProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusCheckWebhookSet_WebhookId",
                table: "StatusCheckWebhookSet",
                column: "WebhookId");

            migrationBuilder.AddForeignKey(
                name: "FK_AlertDeliveryLogSet_AlertProfileSet_AlertProfileId",
                table: "AlertDeliveryLogSet",
                column: "AlertProfileId",
                principalTable: "AlertProfileSet",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_WebhookExecutionLogSet_WebhookSet_WebhookId",
                table: "WebhookExecutionLogSet",
                column: "WebhookId",
                principalTable: "WebhookSet",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AlertDeliveryLogSet_AlertProfileSet_AlertProfileId",
                table: "AlertDeliveryLogSet");

            migrationBuilder.DropForeignKey(
                name: "FK_WebhookExecutionLogSet_WebhookSet_WebhookId",
                table: "WebhookExecutionLogSet");

            migrationBuilder.DropTable(
                name: "BackfillReportSet");

            migrationBuilder.DropTable(
                name: "StatusCheckAlertProfileSet");

            migrationBuilder.DropTable(
                name: "StatusCheckWebhookSet");

            migrationBuilder.DropTable(
                name: "AlertProfileSet");

            migrationBuilder.DropTable(
                name: "WebhookSet");

            migrationBuilder.DropIndex(
                name: "IX_WebhookExecutionLogSet_WebhookId",
                table: "WebhookExecutionLogSet");

            migrationBuilder.DropIndex(
                name: "IX_AlertDeliveryLogSet_AlertProfileId",
                table: "AlertDeliveryLogSet");

            migrationBuilder.DropColumn(
                name: "WebhookId",
                table: "WebhookExecutionLogSet");

            migrationBuilder.DropColumn(
                name: "AlertProfileId",
                table: "AlertDeliveryLogSet");
        }
    }
}
