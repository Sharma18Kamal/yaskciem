using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskFindingLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccessPathId",
                table: "NormalizedRiskFindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "NormalizedRiskFindings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DetectedAt",
                table: "NormalizedRiskFindings",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "EvaluationState",
                table: "NormalizedRiskFindings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "NormalizedRiskFindings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEvaluatedAt",
                table: "NormalizedRiskFindings",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "PermissionId",
                table: "NormalizedRiskFindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleVersion",
                table: "NormalizedRiskFindings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_NormalizedRiskFindings_Fingerprint",
                table: "NormalizedRiskFindings",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_NormalizedRiskFindings_PrincipalId_ResourceId_EffectiveAccessId",
                table: "NormalizedRiskFindings",
                columns: new[] { "PrincipalId", "ResourceId", "EffectiveAccessId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NormalizedRiskFindings_Fingerprint",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropIndex(
                name: "IX_NormalizedRiskFindings_PrincipalId_ResourceId_EffectiveAccessId",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropColumn(
                name: "AccessPathId",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropColumn(
                name: "DetectedAt",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropColumn(
                name: "EvaluationState",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropColumn(
                name: "LastEvaluatedAt",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropColumn(
                name: "PermissionId",
                table: "NormalizedRiskFindings");

            migrationBuilder.DropColumn(
                name: "RuleVersion",
                table: "NormalizedRiskFindings");
        }
    }
}
