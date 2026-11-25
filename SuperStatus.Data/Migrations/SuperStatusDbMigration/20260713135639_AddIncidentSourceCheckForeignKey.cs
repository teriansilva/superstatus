using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class AddIncidentSourceCheckForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #415: existing installs may already carry incidents whose
            // SourceStatusCheckId points at a check that was deleted before this fix
            // (the pre-fix delete path left them orphaned). Remove them BEFORE adding
            // the FK — the constraint can't be created while they dangle, and deleting
            // them is exactly what ON DELETE CASCADE would have done had the FK existed.
            // This one-time cleanup clears any lingering phantom outage and any orphaned
            // incident tied to an already-deleted check.
            migrationBuilder.Sql(@"
                DELETE FROM ""IncidentSet"" i
                WHERE i.""SourceStatusCheckId"" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM ""StatusCheckSet"" s WHERE s.""Id"" = i.""SourceStatusCheckId"");");

            migrationBuilder.AddForeignKey(
                name: "FK_IncidentSet_StatusCheckSet_SourceStatusCheckId",
                table: "IncidentSet",
                column: "SourceStatusCheckId",
                principalTable: "StatusCheckSet",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IncidentSet_StatusCheckSet_SourceStatusCheckId",
                table: "IncidentSet");
        }
    }
}
