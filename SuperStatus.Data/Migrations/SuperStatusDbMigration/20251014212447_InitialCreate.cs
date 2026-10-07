using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IncidentSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuotmaticallyGeneratedReport = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Resolved = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VisibleToPublic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StatusCheckSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    StatusCheckUrl = table.Column<string>(type: "text", nullable: false),
                    IsWebHookOnErrorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    WebHookOnErrorUrl = table.Column<string>(type: "text", nullable: false),
                    ThrottleWebHookToExecuteOnlyEveryXMinutes = table.Column<int>(type: "integer", nullable: false),
                    ExpectedStatusCode = table.Column<int>(type: "integer", nullable: false),
                    ExpectedResponseTimeInMs = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ServiceLogoUrl = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusCheckSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HistoricalStatusDataSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatusCheckId = table.Column<long>(type: "bigint", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: false),
                    ResponseTimeInMs = table.Column<long>(type: "bigint", nullable: false),
                    TimeOfCheckUTC = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CheckFailed = table.Column<bool>(type: "boolean", nullable: false),
                    FailType = table.Column<int>(type: "integer", nullable: false),
                    IncidentId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalStatusDataSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricalStatusDataSet_IncidentSet_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "IncidentSet",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HistoricalStatusDataSet_StatusCheckSet_StatusCheckId",
                        column: x => x.StatusCheckId,
                        principalTable: "StatusCheckSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HistoricalStatusActionSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatusCheckId = table.Column<long>(type: "bigint", nullable: false),
                    HistoricalStatusDataId = table.Column<long>(type: "bigint", nullable: false),
                    ActionType = table.Column<int>(type: "integer", nullable: false),
                    TimeOfExecutionUTC = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalStatusActionSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricalStatusActionSet_HistoricalStatusDataSet_Historica~",
                        column: x => x.HistoricalStatusDataId,
                        principalTable: "HistoricalStatusDataSet",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalStatusActionSet_HistoricalStatusDataId",
                table: "HistoricalStatusActionSet",
                column: "HistoricalStatusDataId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalStatusDataSet_IncidentId",
                table: "HistoricalStatusDataSet",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalStatusDataSet_StatusCheckId",
                table: "HistoricalStatusDataSet",
                column: "StatusCheckId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricalStatusActionSet");

            migrationBuilder.DropTable(
                name: "HistoricalStatusDataSet");

            migrationBuilder.DropTable(
                name: "IncidentSet");

            migrationBuilder.DropTable(
                name: "StatusCheckSet");
        }
    }
}
