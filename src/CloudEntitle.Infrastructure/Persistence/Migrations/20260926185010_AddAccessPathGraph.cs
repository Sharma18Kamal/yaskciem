using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessPathGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EffectiveAccessId",
                table: "AccessPaths",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EngineVersion",
                table: "AccessPaths",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "AccessPaths",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PathStatus",
                table: "AccessPaths",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PermissionId",
                table: "AccessPaths",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "AccessPaths",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ResourceId",
                table: "AccessPaths",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EntityId",
                table: "AccessPathNodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceReference",
                table: "AccessPathNodes",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "AccessPathNodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "AccessPathNodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AccessPathEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccessPathId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelationshipType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    EvidenceReference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Metadata = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPathEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessPathEdges_AccessPaths_AccessPathId",
                        column: x => x.AccessPathId,
                        principalTable: "AccessPaths",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessPaths_EffectiveAccessId",
                table: "AccessPaths",
                column: "EffectiveAccessId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessPaths_Provider_PrincipalId_ResourceId_PermissionId_PathStatus",
                table: "AccessPaths",
                columns: new[] { "Provider", "PrincipalId", "ResourceId", "PermissionId", "PathStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessPathEdges_AccessPathId_FromNodeId_ToNodeId",
                table: "AccessPathEdges",
                columns: new[] { "AccessPathId", "FromNodeId", "ToNodeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessPathEdges");

            migrationBuilder.DropIndex(
                name: "IX_AccessPaths_EffectiveAccessId",
                table: "AccessPaths");

            migrationBuilder.DropIndex(
                name: "IX_AccessPaths_Provider_PrincipalId_ResourceId_PermissionId_PathStatus",
                table: "AccessPaths");

            migrationBuilder.DropColumn(
                name: "EffectiveAccessId",
                table: "AccessPaths");

            migrationBuilder.DropColumn(
                name: "EngineVersion",
                table: "AccessPaths");

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "AccessPaths");

            migrationBuilder.DropColumn(
                name: "PathStatus",
                table: "AccessPaths");

            migrationBuilder.DropColumn(
                name: "PermissionId",
                table: "AccessPaths");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "AccessPaths");

            migrationBuilder.DropColumn(
                name: "ResourceId",
                table: "AccessPaths");

            migrationBuilder.DropColumn(
                name: "EntityId",
                table: "AccessPathNodes");

            migrationBuilder.DropColumn(
                name: "EvidenceReference",
                table: "AccessPathNodes");

            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "AccessPathNodes");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "AccessPathNodes");
        }
    }
}
