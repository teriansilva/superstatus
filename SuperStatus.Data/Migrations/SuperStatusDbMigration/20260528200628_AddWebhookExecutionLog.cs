using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddWebhookExecutionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookExecutionLogSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatusCheckId = table.Column<long>(type: "bigint", nullable: false),
                    HistoricalStatusDataId = table.Column<long>(type: "bigint", nullable: true),
                    AttemptedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TargetUrl = table.Column<string>(type: "text", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: false),
                    ResponseTimeMs = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookExecutionLogSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookExecutionLogSet_HistoricalStatusDataSet_HistoricalSt~",
                        column: x => x.HistoricalStatusDataId,
                        principalTable: "HistoricalStatusDataSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WebhookExecutionLogSet_StatusCheckSet_StatusCheckId",
                        column: x => x.StatusCheckId,
                        principalTable: "StatusCheckSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookExecutionLogSet_AttemptedUtc",
                table: "WebhookExecutionLogSet",
                column: "AttemptedUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookExecutionLogSet_HistoricalStatusDataId",
                table: "WebhookExecutionLogSet",
                column: "HistoricalStatusDataId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookExecutionLogSet_Outcome",
                table: "WebhookExecutionLogSet",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookExecutionLogSet_StatusCheckId_AttemptedUtc",
                table: "WebhookExecutionLogSet",
                columns: new[] { "StatusCheckId", "AttemptedUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookExecutionLogSet");
        }
    }
}
