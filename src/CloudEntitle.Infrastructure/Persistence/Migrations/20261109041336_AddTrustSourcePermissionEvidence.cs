using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrustSourcePermissionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequiredSourcePermissionPattern",
                table: "ToxicCombinationTrustPathEvidence",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresSourcePermission",
                table: "ToxicCombinationTrustPathEvidence",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiredSourcePermissionPattern",
                table: "ToxicCombinationTrustPathEvidence");

            migrationBuilder.DropColumn(
                name: "RequiresSourcePermission",
                table: "ToxicCombinationTrustPathEvidence");
        }
    }
}
