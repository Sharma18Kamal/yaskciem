using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemediationPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemediationPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemediationActions",
                columns: table => new
                {
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationActions", x => new { x.PlanId, x.Id });
                    table.ForeignKey(
                        name: "FK_RemediationActions_RemediationPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "RemediationPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemediationApprovals",
                columns: table => new
                {
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationApprovals", x => x.PlanId);
                    table.ForeignKey(
                        name: "FK_RemediationApprovals_RemediationPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "RemediationPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemediationAuditRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationAuditRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemediationAuditRecords_RemediationPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "RemediationPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemediationExecutions",
                columns: table => new
                {
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationExecutions", x => new { x.PlanId, x.Id });
                    table.ForeignKey(
                        name: "FK_RemediationExecutions_RemediationPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "RemediationPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemediationRollbacks",
                columns: table => new
                {
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationRollbacks", x => new { x.PlanId, x.Id });
                    table.ForeignKey(
                        name: "FK_RemediationRollbacks_RemediationPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "RemediationPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemediationActions_PlanId_Position",
                table: "RemediationActions",
                columns: new[] { "PlanId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_RemediationAuditRecords_PlanId_Timestamp",
                table: "RemediationAuditRecords",
                columns: new[] { "PlanId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_RemediationPlans_CreatedAt",
                table: "RemediationPlans",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RemediationPlans_OrganizationId_Status",
                table: "RemediationPlans",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RemediationRollbacks_PlanId_Position",
                table: "RemediationRollbacks",
                columns: new[] { "PlanId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemediationActions");

            migrationBuilder.DropTable(
                name: "RemediationApprovals");

            migrationBuilder.DropTable(
                name: "RemediationAuditRecords");

            migrationBuilder.DropTable(
                name: "RemediationExecutions");

            migrationBuilder.DropTable(
                name: "RemediationRollbacks");

            migrationBuilder.DropTable(
                name: "RemediationPlans");
        }
    }
}
