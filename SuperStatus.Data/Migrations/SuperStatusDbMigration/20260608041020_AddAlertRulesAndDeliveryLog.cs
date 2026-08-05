using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddAlertRulesAndDeliveryLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AlertLastFiredUtc",
                table: "StatusCheckSet",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlertOnFailureThreshold",
                table: "StatusCheckSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AlertOnOutageMinutes",
                table: "StatusCheckSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AlertOnRecovery",
                table: "StatusCheckSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AlertThrottleMinutes",
                table: "StatusCheckSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AlertedOutageDownSinceUtc",
                table: "StatusCheckSet",
                type: "timestamp with time zone",
                nullable: true);

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

            migrationBuilder.AddColumn<bool>(
                name: "WebPushAlertsEnabled",
                table: "StatusCheckSet",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AlertDeliveryLogSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatusCheckId = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    AttemptedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Target = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDeliveryLogSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertDeliveryLogSet_StatusCheckSet_StatusCheckId",
                        column: x => x.StatusCheckId,
                        principalTable: "StatusCheckSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveryLogSet_AttemptedUtc",
                table: "AlertDeliveryLogSet",
                column: "AttemptedUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveryLogSet_Outcome",
                table: "AlertDeliveryLogSet",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveryLogSet_StatusCheckId_AttemptedUtc",
                table: "AlertDeliveryLogSet",
                columns: new[] { "StatusCheckId", "AttemptedUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertDeliveryLogSet");

            migrationBuilder.DropColumn(
                name: "AlertLastFiredUtc",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "AlertOnFailureThreshold",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "AlertOnOutageMinutes",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "AlertOnRecovery",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "AlertThrottleMinutes",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "AlertedOutageDownSinceUtc",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "EmailAlertsEnabled",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "EmailRecipients",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "WebPushAlertsEnabled",
                table: "StatusCheckSet");
        }
    }
}
