using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperTalk.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExtractedFileDataSet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    AiSummary = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    FileType = table.Column<string>(type: "text", nullable: false),
                    AnalysisStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnalysisEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractedFileDataSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExtractedPageDataSet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtractedFileDataId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    AnalysisStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnalysisEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractedPageDataSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtractedPageDataSet_ExtractedFileDataSet_ExtractedFileData~",
                        column: x => x.ExtractedFileDataId,
                        principalTable: "ExtractedFileDataSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiResponseDataSet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtractedPageDataId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelName = table.Column<string>(type: "text", nullable: false),
                    PromptUsed = table.Column<string>(type: "text", nullable: false),
                    AiAnalysisType = table.Column<string>(type: "text", nullable: false),
                    AiResponseText = table.Column<string>(type: "text", nullable: false),
                    AnalysisStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnalysisEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiResponseDataSet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiResponseDataSet_ExtractedPageDataSet_ExtractedPageDataId",
                        column: x => x.ExtractedPageDataId,
                        principalTable: "ExtractedPageDataSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiResponseDataSet_ExtractedPageDataId",
                table: "AiResponseDataSet",
                column: "ExtractedPageDataId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractedPageDataSet_ExtractedFileDataId",
                table: "ExtractedPageDataSet",
                column: "ExtractedFileDataId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiResponseDataSet");

            migrationBuilder.DropTable(
                name: "ExtractedPageDataSet");

            migrationBuilder.DropTable(
                name: "ExtractedFileDataSet");
        }
    }
}
