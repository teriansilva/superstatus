using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddStatusCheckGridFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Seed",
                table: "StatusCheckSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "StatusCheckSet",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "NOW()");

            // Backfill Seed with a per-row random int for existing rows
            // (rows with the default 0 placed by AddColumn). New rows
            // inserted by application code always supply Seed explicitly
            // (see StatusCheckService.AddOrUpdateStatusCheck).
            migrationBuilder.Sql(@"
                UPDATE ""StatusCheckSet""
                SET ""Seed"" = (FLOOR(RANDOM() * 2147483647))::int
                WHERE ""Seed"" = 0;
            ");

            // Backfill Created with the earliest TimeOfCheckUTC observed
            // for the row, falling back to NOW() when no history exists.
            // This way an existing check that has been running for months
            // immediately renders at its correct growth tier in the Grid.
            migrationBuilder.Sql(@"
                UPDATE ""StatusCheckSet"" sc
                SET ""Created"" = COALESCE((
                    SELECT MIN(h.""TimeOfCheckUTC"")
                    FROM ""HistoricalStatusDataSet"" h
                    WHERE h.""StatusCheckId"" = sc.""Id""
                ), NOW());
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Created", table: "StatusCheckSet");
            migrationBuilder.DropColumn(name: "Seed", table: "StatusCheckSet");
        }
    }
}
