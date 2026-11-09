using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddToxicCombinationEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToxicCombinationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstPermissionPattern = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SecondPermissionPattern = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ResourceRequirement = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstRequiresPrivilegedAccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    SecondRequiresPrivilegedAccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequireSameResource = table.Column<bool>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    RiskScore = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Recommendation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToxicCombinationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToxicCombinationFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    RiskScore = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluationState = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ResourceRelation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EvidenceSummary = table.Column<string>(type: "TEXT", nullable: false),
                    Recommendation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstDetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastEvaluatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToxicCombinationFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToxicCombinationFindings_ToxicCombinationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "ToxicCombinationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermissionCombinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstPermissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecondPermissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstEffectiveAccessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecondEffectiveAccessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstAction = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SecondAction = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ProviderSemantics = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ScopeAssessment = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionCombinations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermissionCombinations_ToxicCombinationFindings_FindingId",
                        column: x => x.FindingId,
                        principalTable: "ToxicCombinationFindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ToxicCombinationEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EffectiveAccessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AccessPathId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PermissionAction = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AssignmentType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsDirect = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsInherited = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsConditional = table.Column<bool>(type: "INTEGER", nullable: false),
                    EvaluationStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceEvidence = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToxicCombinationEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToxicCombinationEvidence_ToxicCombinationFindings_FindingId",
                        column: x => x.FindingId,
                        principalTable: "ToxicCombinationFindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ToxicCombinationRecommendations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false),
                    Executable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToxicCombinationRecommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToxicCombinationRecommendations_ToxicCombinationFindings_FindingId",
                        column: x => x.FindingId,
                        principalTable: "ToxicCombinationFindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionCombinations_FindingId_FirstEffectiveAccessId_SecondEffectiveAccessId",
                table: "PermissionCombinations",
                columns: new[] { "FindingId", "FirstEffectiveAccessId", "SecondEffectiveAccessId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationEvidence_FindingId_EffectiveAccessId",
                table: "ToxicCombinationEvidence",
                columns: new[] { "FindingId", "EffectiveAccessId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationEvidence_PermissionId_ResourceId",
                table: "ToxicCombinationEvidence",
                columns: new[] { "PermissionId", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationFindings_CloudAccountId_Provider_PrincipalId_Severity",
                table: "ToxicCombinationFindings",
                columns: new[] { "CloudAccountId", "Provider", "PrincipalId", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationFindings_Fingerprint",
                table: "ToxicCombinationFindings",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationFindings_RuleId_LastEvaluatedAtUtc",
                table: "ToxicCombinationFindings",
                columns: new[] { "RuleId", "LastEvaluatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationRecommendations_FindingId",
                table: "ToxicCombinationRecommendations",
                column: "FindingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationRules_Key",
                table: "ToxicCombinationRules",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToxicCombinationRules_Provider_Enabled",
                table: "ToxicCombinationRules",
                columns: new[] { "Provider", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PermissionCombinations");

            migrationBuilder.DropTable(
                name: "ToxicCombinationEvidence");

            migrationBuilder.DropTable(
                name: "ToxicCombinationRecommendations");

            migrationBuilder.DropTable(
                name: "ToxicCombinationFindings");

            migrationBuilder.DropTable(
                name: "ToxicCombinationRules");
        }
    }
}
