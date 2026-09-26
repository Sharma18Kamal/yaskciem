using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccessPathId",
                table: "Recommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Changes",
                table: "Recommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CloudAccountId",
                table: "Recommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EffectiveAccessId",
                table: "Recommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EntitlementId",
                table: "Recommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EvaluationState",
                table: "Recommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "Recommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "Recommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Impact",
                table: "Recommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ImpactState",
                table: "Recommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PrincipalId",
                table: "Recommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "Recommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecommendationType",
                table: "Recommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ResourceId",
                table: "Recommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Recommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Recommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "AccessPathId",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Changes",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CloudAccountId",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EffectiveAccessId",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EntitlementId",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EvaluationState",
                table: "NormalizedRecommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Impact",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ImpactState",
                table: "NormalizedRecommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PrincipalId",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "NormalizedRecommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecommendationType",
                table: "NormalizedRecommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ResourceId",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "NormalizedRecommendations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "NormalizedRecommendations",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_NormalizedRecommendations_Fingerprint",
                table: "NormalizedRecommendations",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_NormalizedRecommendations_RecommendationType_Provider_Status_EvaluationState",
                table: "NormalizedRecommendations",
                columns: new[] { "RecommendationType", "Provider", "Status", "EvaluationState" });

            migrationBuilder.CreateIndex(
                name: "IX_NormalizedRecommendations_RiskFindingId_EffectiveAccessId_AccessPathId",
                table: "NormalizedRecommendations",
                columns: new[] { "RiskFindingId", "EffectiveAccessId", "AccessPathId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NormalizedRecommendations_Fingerprint",
                table: "NormalizedRecommendations");

            migrationBuilder.DropIndex(
                name: "IX_NormalizedRecommendations_RecommendationType_Provider_Status_EvaluationState",
                table: "NormalizedRecommendations");

            migrationBuilder.DropIndex(
                name: "IX_NormalizedRecommendations_RiskFindingId_EffectiveAccessId_AccessPathId",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "AccessPathId",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "Changes",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "CloudAccountId",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "EffectiveAccessId",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "EntitlementId",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "EvaluationState",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "Impact",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "ImpactState",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "PrincipalId",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "RecommendationType",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "ResourceId",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Recommendations");

            migrationBuilder.DropColumn(
                name: "AccessPathId",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "Changes",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "CloudAccountId",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "EffectiveAccessId",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "EntitlementId",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "EvaluationState",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "Impact",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "ImpactState",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "PrincipalId",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "RecommendationType",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "ResourceId",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "NormalizedRecommendations");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "NormalizedRecommendations");
        }
    }
}
