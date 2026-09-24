using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddSlaEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SlaId",
                table: "StatusCheckSet",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SlaSet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    TargetUptimePercent = table.Column<double>(type: "double precision", nullable: false),
                    CriticalUptimePercent = table.Column<double>(type: "double precision", nullable: false),
                    SlowThresholdMs = table.Column<long>(type: "bigint", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaSet", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatusCheckSet_SlaId",
                table: "StatusCheckSet",
                column: "SlaId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaSet_IsDefault_SingleDefault",
                table: "SlaSet",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.AddForeignKey(
                name: "FK_StatusCheckSet_SlaSet_SlaId",
                table: "StatusCheckSet",
                column: "SlaId",
                principalTable: "SlaSet",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StatusCheckSet_SlaSet_SlaId",
                table: "StatusCheckSet");

            migrationBuilder.DropTable(
                name: "SlaSet");

            migrationBuilder.DropIndex(
                name: "IX_StatusCheckSet_SlaId",
                table: "StatusCheckSet");

            migrationBuilder.DropColumn(
                name: "SlaId",
                table: "StatusCheckSet");
        }
    }
}
