using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReportExecutionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportExecutionHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OutputFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportExecutionHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportExecutionHistory_Category_GeneratedAtUtc",
                table: "ReportExecutionHistory",
                columns: new[] { "Category", "GeneratedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportExecutionHistory_GeneratedAtUtc",
                table: "ReportExecutionHistory",
                column: "GeneratedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportExecutionHistory");
        }
    }
}
