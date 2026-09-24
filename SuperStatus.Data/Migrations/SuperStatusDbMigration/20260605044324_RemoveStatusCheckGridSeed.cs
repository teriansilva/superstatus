using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperStatus.Data.Migrations.SuperStatusDbMigration
{
    /// <inheritdoc />
    public partial class RemoveStatusCheckGridSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Seed",
                table: "StatusCheckSet");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Seed",
                table: "StatusCheckSet",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
