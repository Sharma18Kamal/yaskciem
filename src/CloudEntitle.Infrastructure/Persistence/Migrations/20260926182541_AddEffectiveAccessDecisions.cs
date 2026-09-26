using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEffectiveAccessDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EffectiveAccesses_PermissionId_IsPrivileged",
                table: "EffectiveAccesses");

            migrationBuilder.AddColumn<string>(
                name: "AccessSource",
                table: "EffectiveAccesses",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Decision",
                table: "EffectiveAccesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationDetails",
                table: "EffectiveAccesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EvaluationStatus",
                table: "EffectiveAccesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "EffectiveAccesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsConditional",
                table: "EffectiveAccesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "EffectiveAccesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_EffectiveAccesses_CloudAccountId_EvaluationStatus_CalculatedAt",
                table: "EffectiveAccesses",
                columns: new[] { "CloudAccountId", "EvaluationStatus", "CalculatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EffectiveAccesses_PermissionId_IsPrivileged_Decision",
                table: "EffectiveAccesses",
                columns: new[] { "PermissionId", "IsPrivileged", "Decision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EffectiveAccesses_CloudAccountId_EvaluationStatus_CalculatedAt",
                table: "EffectiveAccesses");

            migrationBuilder.DropIndex(
                name: "IX_EffectiveAccesses_PermissionId_IsPrivileged_Decision",
                table: "EffectiveAccesses");

            migrationBuilder.DropColumn(
                name: "AccessSource",
                table: "EffectiveAccesses");

            migrationBuilder.DropColumn(
                name: "Decision",
                table: "EffectiveAccesses");

            migrationBuilder.DropColumn(
                name: "EvaluationDetails",
                table: "EffectiveAccesses");

            migrationBuilder.DropColumn(
                name: "EvaluationStatus",
                table: "EffectiveAccesses");

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "EffectiveAccesses");

            migrationBuilder.DropColumn(
                name: "IsConditional",
                table: "EffectiveAccesses");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "EffectiveAccesses");

            migrationBuilder.CreateIndex(
                name: "IX_EffectiveAccesses_PermissionId_IsPrivileged",
                table: "EffectiveAccesses",
                columns: new[] { "PermissionId", "IsPrivileged" });
        }
    }
}
