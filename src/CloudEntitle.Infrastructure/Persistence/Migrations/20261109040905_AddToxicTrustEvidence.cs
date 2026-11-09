using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddToxicTrustEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireTrustPath",
                table: "ToxicCombinationRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ToxicCombinationTrustPathEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceProvider = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceCloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourcePrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetProvider = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetCloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelationshipType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    EvidenceSource = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    EvidenceReference = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ConditionEvidence = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToxicCombinationTrustPathEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToxicCombinationTrustPathEvidence_ToxicCombinationFindings_FindingId",
                        column: x => x.FindingId,
                        principalTable: "ToxicCombinationFindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationTrustPathEvidence_FindingId_Sequence",
                table: "ToxicCombinationTrustPathEvidence",
                columns: new[] { "FindingId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationTrustPathEvidence_SourceCloudAccountId_SourcePrincipalId_TargetCloudAccountId_TargetPrincipalId",
                table: "ToxicCombinationTrustPathEvidence",
                columns: new[] { "SourceCloudAccountId", "SourcePrincipalId", "TargetCloudAccountId", "TargetPrincipalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToxicCombinationTrustPathEvidence");

            migrationBuilder.DropColumn(
                name: "RequireTrustPath",
                table: "ToxicCombinationRules");
        }
    }
}
